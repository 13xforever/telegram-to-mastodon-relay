﻿using System.Collections.Concurrent;
using MeduzaRepost.Database;
using NLog;
using NLog.Fluent;
using TL;
using WTelegram;

namespace MeduzaRepost;

public sealed class TelegramReader: IObservable<TgEvent>, IDisposable
{
    private static readonly ILogger Log = Config.Log.WithPrefix("telegram");
    private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "update_manager_state.json");
    private readonly ConcurrentDictionary<IObserver<TgEvent>, Unsubscriber> subscribers = new();
    private readonly BotDb db = new();

    private Channel channel = null!;
    internal readonly Client Client = new(Config.Get);
    internal readonly UpdateManager UpdateManager;
    
    static TelegramReader() => Helpers.Log = OnTelegramLog;

    public TelegramReader()
    {
        UpdateManager = Client.WithUpdateManager(OnUpdate, Path.Combine(StatePath, StatePath));
        UpdateManager.InactivityThreshold = Config.WatchdogThreshold;
    }

    private static void OnTelegramLog(int level, string message)
    {
        Config.SpamLog.Log(LogLevel.FromOrdinal(level), message);
        if (message.Contains("MESSAGE_ID_INVALID"))
            Config.Cts.Cancel();
    }

    public async Task Run()
    {
        Log.Info("Trying to log into telegram account…");
        var bot = await Client.LoginUserIfNeeded().ConfigureAwait(false);
        Log.Info($"We are logged in as {bot} (id {bot.id}) on telegram");
        var chats = await Client.Messages_GetAllChats().ConfigureAwait(false);
        Log.Debug($"Open chat count: {chats.chats.Count}");
        if (chats.chats.Values.FirstOrDefault(chat => chat is Channel { username: "meduzalive" }) is not Channel ch)
        {
            Log.Error("Meduza channel (@meduzalive) is not available");
            return;
        }

        Client.OnOther += OnMiscUpdate;
        // check and init saved pts value if needed
        channel = ch;
        Log.Info($"Reading channel #{channel.ID}: {channel.Title}");
        if (db.BotState.FirstOrDefault(s => s.Key == "pts") is { Value: { Length: > 0 } ptsVal } state
            && int.TryParse(ptsVal, out var savedPts)
            && savedPts > 0)
        {
            // check missed updates
            Log.Info($"Checking missed channel updates since pts {savedPts}…");
            var diffPts = savedPts;
            long lastGroupId = 0;
            while (await Client.Updates_GetChannelDifference(channel, null, diffPts).ConfigureAwait(false) is {} baseDiff)
            {
                if (baseDiff is Updates_ChannelDifferenceEmpty)
                    break;
                if (baseDiff is Updates_ChannelDifference diff)
                {
                    Log.Info($"Got {diff.NewMessages.Length} new messages and {diff.OtherUpdates.Length} other updates, {(diff.Final ? "" : "not ")}final");
                    foreach (var message in diff.NewMessages.OfType<Message>().OrderBy(m => m.Date))
                    {
                        if (message.flags.HasFlag(Message.Flags.has_grouped_id))
                        {
                            if (message.grouped_id == lastGroupId)
                                continue;
                            
                            lastGroupId = message.grouped_id;
                            var group = diff.NewMessages.OfType<Message>().Where(m => m.grouped_id == lastGroupId).ToList();
                            var groupLink = await Client.Channels_ExportMessageLink(channel, group[0].id, true).ConfigureAwait(false);
                            Push(new(TgEventType.Post, new(group), diffPts, groupLink.link));
                            Log.Info($"Assembled message group {lastGroupId} of {group.Count} messages from channel diff");
                            continue;
                        }
                        lastGroupId = 0;
                        var link = await Client.Channels_ExportMessageLink(channel, message.id).ConfigureAwait(false);
                        Push(new(TgEventType.Post, new(message), diffPts, link.link));
                    }
                    if (diff.OtherUpdates.Length > 0)
                        await OnUpdate(diff.OtherUpdates).ConfigureAwait(false);
                    diffPts = diff.pts;
                }
                else
                    Log.Warn($"Unsupported channel difference update of type {baseDiff.GetType().Name}");
                if (baseDiff.Final)
                    break;
            }
            savedPts = diffPts;
            state.Value = savedPts.ToString();
            Log.Info("All missed updates are processed");
        }
        else
        {
            Log.Info("No saved pts value, initializing state");
            var dialogs = await Client.Messages_GetPeerDialogs(channel.ToInputPeer()).ConfigureAwait(false);
            if (dialogs.dialogs is not [Dialog dialog] || dialog.Peer.ID != channel.ID)
            {
                Log.Error("Failed to fetch current channel status");
                return;
            }

            savedPts = dialog.pts;
            Log.Info($"Got initial pts value: {savedPts}");
            db.BotState.Add(new() { Key = "pts", Value = savedPts.ToString() });
        }
        await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);

        Log.Info("Reading telegram pins…");
        var pins = await Client.Messages_Search<InputMessagesFilterPinned>(channel.ToInputPeer()).ConfigureAwait(false);
        var pinnedMessages = pins.Messages.Cast<Message>().ToList();
        Push(new(TgEventType.Pin, new(pinnedMessages), savedPts - pinnedMessages.Count));
        Log.Info($"Got {pinnedMessages.Count} pin{(pinnedMessages.Count == 1 ? "" : "s")}");
        
        Log.Info("Listening to live telegram updates…");
        //Client.OnUpdates += OnUpdate;
      
        while (!Config.Cts.IsCancellationRequested)
        {
            UpdateManager.SaveState(StatePath);
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private Task OnUpdate(UpdatesBase updates) => OnUpdate(updates.UpdateList);
    private async Task OnUpdate(Update[] updates)
    {
        try
        {
            if (updates.Length > 1)
                Log.Info($"Received {updates.Length} updates");
            else
                Log.Debug($"Received {updates.Length} update");
            foreach (var update in updates.OrderBy(u => u.GetPts()))
                await OnUpdate(update).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e);
            throw;
        }
    }

    private readonly ConcurrentQueue<Message> msgGroup = new();
    private async Task OnUpdate(Update update)
    {
        switch (update)
        {
            case UpdateNewMessage u when u.message.Peer.ID == channel.ID:
            {
                Log.Debug($"Processing NewMessage update, pts={u.pts}, count={u.pts_count}");
                if (u.message is not Message msg)
                {
                    Log.Warn($"Invalid message type {u.message.GetType().Name} in {nameof(UpdateNewMessage)}, skipping");
                    return;
                }

                if (u.pts_count > 1)
                    Log.Warn($"Got update with large pts_count {u.pts_count} for message {u.message.ID}, group id {msg.grouped_id}");
                if (!msgGroup.IsEmpty
                    && (!msg.flags.HasFlag(Message.Flags.has_grouped_id)
                        || msgGroup.TryPeek(out var gmsg) && gmsg.grouped_id != msg.grouped_id))
                    await DrainMsgGroupQueueAsync().ConfigureAwait(false);
                if (msg.flags.HasFlag(Message.Flags.has_grouped_id))
                {
                    msgGroup.Enqueue(msg);
                    Log.Debug($"Adding message {msg.id} to the group queue {msg.grouped_id}");
                    if (!string.IsNullOrEmpty(msg.message))
                        await DrainMsgGroupQueueAsync().ConfigureAwait(false);
                }
                else
                {
                    var link = await Client.Channels_ExportMessageLink(channel, u.message.ID).ConfigureAwait(false);
                    Push(new(TgEventType.Post, new(0, 1, msg), u.pts, link.link));
                }
                break;
            }
            case UpdateEditMessage u when u.message.Peer.ID == channel.ID:
            {
                Log.Debug($"Processing EditMessage update, pts={u.pts}, count={u.pts_count}");
                var link = await Client.Channels_ExportMessageLink(
                    channel,
                    u.message.ID, 
                    ((Message)u.message).flags.HasFlag(Message.Flags.has_grouped_id)
                ).ConfigureAwait(false);
                Push(new(TgEventType.Edit, new((Message)u.message), u.pts, link.link));
                break;
            }
            case UpdateDeleteMessages u:
            {
                Log.Debug($"Processing DeleteMessage update, pts={u.pts}, count={u.pts_count}");
                Push(new(TgEventType.Delete, new(u.messages), u.pts));
                break;
            }
            case UpdatePinnedChannelMessages u:
            {
                Log.Debug($"Processing PinnedMessages update, pts={u.pts}, count={u.pts_count}");
                Push(new(TgEventType.Pin, new(u.messages), u.pts));
                break;
            }
            default:
            {
                Log.Debug($"Ignoring update of type {update.GetType().Name}");
                break;
            }
        }
    }

    private async Task DrainMsgGroupQueueAsync()
    {
        if (msgGroup.IsEmpty)
            throw new InvalidOperationException("Expected at least one message in the group, but got none");
        
        var groupedUpdates = msgGroup.ToList();
        var msg = groupedUpdates[^1];
        var gid = msg.grouped_id;
        var group = new MessageGroup(gid, groupedUpdates);
        var groupLink = await Client.Channels_ExportMessageLink(channel, msg.id, true).ConfigureAwait(false);
        Log.Info($"Created new message group {group.Id} of expected size {group.Expected}");
        Push(new(TgEventType.Post, group, msg.GetPts(), groupLink.link));
        msgGroup.Clear();
    }

    private async Task OnMiscUpdate(IObject arg)
    {
        try
        {
            if (arg is not ReactorError err)
            {
                Log.Debug($"Ignoring misc update of type {arg.GetType().Name}");
                return;
            }

            Log.Error(err.Exception, $"⛔ {err.Exception.Message}");
        }
        catch (Exception e)
        {
            Log.Error(e);
            throw;
        }
    }


    private void Push(TgEvent evt)
    {
        foreach (var observer in subscribers.Keys)
            try
            {
                observer.OnNext(evt);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to push telegram event");
            }
    }

    public void Dispose()
    {
        UpdateManager.SaveState(StatePath);
        foreach (var observer in subscribers.Keys)
            observer.OnCompleted();
        Client.Dispose();
        db.Dispose();
    }

    public IDisposable Subscribe(IObserver<TgEvent> observer)
    {
        if (!subscribers.TryGetValue(observer, out var unsubscriber))
        {
            unsubscriber = new(subscribers, observer);
            if (!subscribers.TryAdd(observer, unsubscriber))
                throw new InvalidOperationException("Observer is already subscribed");
        }
        return unsubscriber;
    }

    private record Unsubscriber(ConcurrentDictionary<IObserver<TgEvent>, Unsubscriber> Subscribers, IObserver<TgEvent> Observer) : IDisposable
    {
        public void Dispose() => Subscribers.TryRemove(Observer, out _);
    }
}