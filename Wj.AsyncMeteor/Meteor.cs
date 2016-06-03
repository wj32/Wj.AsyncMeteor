using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wj.AsyncMeteor
{
    public class Meteor : IDisposable
    {
        private enum InQueueEntryType
        {
            Message,
            Callback
        }

        private class InQueueEntry
        {
            public InQueueEntryType Type;
            public JObject Message;
            public Func<Task> Callback;
        }

        private class PendingCall
        {
            public string Method;
            public object[] Values;
            public Action<ErrorOrResult<dynamic>> Callback;
        }

        public enum SubscriptionState
        {
            Initialized,
            Ready,
            Unsubscribed
        }

        public class Subscription
        {
            private Meteor _owner;

            internal Subscription(Meteor owner, string id)
            {
                _owner = owner;
                Id = id;
            }

            public string Id { get; private set; }

            public SubscriptionState State { get; internal set; }

            public dynamic Error { get; internal set; }

            internal Func<Subscription, Task> StateChangedCallback { get; set; }

            public async Task Unsubscribe()
            {
                if (State != SubscriptionState.Unsubscribed)
                    await _owner.Unsubscribe(Id);
            }
        }

        public class Collection
        {
            public delegate void AddedDelegate(string id, IDictionary<string, object> fields);
            public delegate void ChangedDelegate(string id, dynamic oldObject, IDictionary<string, object> fields, string[] cleared);
            public delegate void RemovedDelegate(string id, dynamic oldObject);

            private Meteor _owner;
            private Dictionary<string, dynamic> _data = new Dictionary<string, dynamic>();

            public event AddedDelegate Added;
            public event ChangedDelegate Changed;
            public event RemovedDelegate Removed;

            internal Collection(Meteor owner, string collectionName)
            {
                _owner = owner;
                Name = collectionName;
            }

            public string Name { get; private set; }

            public IReadOnlyDictionary<string, dynamic> Data { get { return _data; } }

            public void Unregister()
            {
                _owner.UnregisterCollection(Name);
            }

            internal void ApplyAdded(string id, JObject fields)
            {
                IDictionary<string, object> o = new ExpandoObject();

                foreach (var field in fields.Properties())
                    o[field.Name] = field.Value;

                _data.Add(id, o);

                var added = this.Added;
                if (added != null)
                    added(id, fields.Properties().ToDictionary(p => p.Name, p => (object)p.Value));
            }

            internal void ApplyChanged(string id, JObject fields, JArray cleared)
            {
                IDictionary<string, object> o = _data[id];
                var changed = this.Changed;
                IDictionary<string, object> oldObject = null;

                if (changed != null)
                {
                    oldObject = new ExpandoObject();
                    foreach (var pair in o)
                        oldObject.Add(pair);
                }

                foreach (var field in fields.Properties())
                    o[field.Name] = field.Value;

                if (cleared != null)
                {
                    foreach (var fieldName in cleared)
                        o.Remove((string)fieldName);
                }

                if (changed != null)
                {
                    changed(
                        id,
                        oldObject,
                        fields.Properties().ToDictionary(p => p.Name, p => (object)p.Value),
                        cleared != null ? cleared.Cast<string>().ToArray() : null
                        );
                }
            }

            internal void ApplyRemoved(string id)
            {
                var oldObject = _data[id];
                _data.Remove(id);

                var removed = this.Removed;
                if (removed != null)
                    removed(id, oldObject);
            }
        }

        public delegate void ErrorDelegate(Exception ex);

        private const int BufferSize = 60 * 1024;
        private const string SubscriptionIdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        private Uri _uri;
        private ClientWebSocket _socket = new ClientWebSocket();
        private long _nextId = 1;
        private Random _random = new Random();
        private AsyncCollection<InQueueEntry> _inQueue = new AsyncCollection<InQueueEntry>();
        private AsyncCollection<string> _outQueue = new AsyncCollection<string>();
        private CancellationTokenSource _processQueueCancellationTokenSource = new CancellationTokenSource();
        private Dictionary<string, PendingCall> _pendingCalls = new Dictionary<string, PendingCall>();
        private Dictionary<string, Subscription> _subscriptions = new Dictionary<string, Subscription>();
        private Dictionary<string, Collection> _collections = new Dictionary<string, Collection>();

        public event ErrorDelegate Error;

        public Meteor(Uri uri)
        {
            _uri = uri;
        }

        public void Dispose()
        {
            _processQueueCancellationTokenSource.Cancel();
            _socket.Dispose();
        }

        public WebSocketState SocketState
        {
            get { return _socket.State; }
        }

        private async Task SendMessage(object message)
        {
            await _outQueue.AddAsync(JsonConvert.SerializeObject(message));
        }

        public async Task Connect()
        {
            await Connect(CancellationToken.None);
        }

        public async Task Connect(CancellationToken cancellationToken)
        {
            if (_socket.State == WebSocketState.Open)
                return;

            await _socket.ConnectAsync(_uri, cancellationToken);

            ConsumeSocket().Forget();
            ConsumeInQueue().Forget();
            ConsumeOutQueue().Forget();

            await SendMessage(new
            {
                msg = "connect",
                version = "1",
                support = new string[] { "1", "pre1" }
            });
        }

        private async Task ConsumeSocket()
        {
            var buffer = new byte[BufferSize];
            var memoryStream = new MemoryStream(BufferSize);

            // TODO: Handle reconnection.
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _processQueueCancellationTokenSource.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    memoryStream.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                        await _inQueue.AddAsync(new InQueueEntry { Type = InQueueEntryType.Message, Message = JObject.Parse(message) });

                        memoryStream.Dispose();
                        memoryStream = new MemoryStream(BufferSize);
                    }
                }
            }
            catch (OperationCanceledException)
            { }
            catch (Exception ex)
            {
                var error = this.Error;
                if (error != null)
                    error(ex);
            }
        }

        private async Task ConsumeInQueue()
        {
            try
            {
                while (true)
                {
                    var entry = await _inQueue.TakeAsync(_processQueueCancellationTokenSource.Token);

                    switch (entry.Type)
                    {
                        case InQueueEntryType.Message:
                            await ProcessMessage(entry.Message);
                            break;
                        case InQueueEntryType.Callback:
                            await entry.Callback();
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            { }
        }

        private async Task ConsumeOutQueue()
        {
            try
            {
                while (true)
                {
                    var text = await _outQueue.TakeAsync(_processQueueCancellationTokenSource.Token);

                    try
                    {
                        await _socket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
                            WebSocketMessageType.Text,
                            true,
                            _processQueueCancellationTokenSource.Token
                            );
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var error = this.Error;
                        if (error != null)
                            error(ex);
                    }
                }
            }
            catch (OperationCanceledException)
            { }
        }

        private async Task ProcessMessage(JObject message)
        {
            switch ((string)message["msg"])
            {
                case "ping":
                    await SendMessage(new
                    {
                        msg = "pong"
                    });
                    break;
                case "result":
                    {
                        var id = (string)message["id"];
                        PendingCall pendingCall;
                        if (_pendingCalls.TryGetValue(id, out pendingCall))
                        {
                            pendingCall.Callback(new ErrorOrResult<dynamic> { Error = message["error"], Result = message["result"] });
                            _pendingCalls.Remove(id);
                        }
                    }
                    break;
                case "nosub":
                    {
                        var id = (string)message["id"];
                        Subscription subscription;
                        if (_subscriptions.TryGetValue(id, out subscription))
                        {
                            subscription.State = SubscriptionState.Unsubscribed;
                            subscription.Error = message["error"];
                            if (subscription.StateChangedCallback != null)
                                await subscription.StateChangedCallback(subscription);

                            _subscriptions.Remove(id);
                        }
                    }
                    break;
                case "added":
                    {
                        var collectionName = (string)message["collection"];
                        var id = (string)message["id"];
                        var fields = (JObject)message["fields"];
                        Collection collection;
                        if (_collections.TryGetValue(collectionName, out collection))
                            collection.ApplyAdded(id, fields);
                    }
                    break;
                case "changed":
                    {
                        var collectionName = (string)message["collection"];
                        var id = (string)message["id"];
                        var fields = (JObject)message["fields"];
                        var cleared = (JArray)message["cleared"];
                        Collection collection;
                        if (_collections.TryGetValue(collectionName, out collection))
                            collection.ApplyChanged(id, fields, cleared);
                    }
                    break;
                case "removed":
                    {
                        var collectionName = (string)message["collection"];
                        var id = (string)message["id"];
                        Collection collection;
                        if (_collections.TryGetValue(collectionName, out collection))
                            collection.ApplyRemoved(id);
                    }
                    break;
                case "ready":
                    {
                        var subs = message["subs"];
                        foreach (var id in subs)
                        {
                            Subscription subscription;
                            if (_subscriptions.TryGetValue((string)id, out subscription))
                            {
                                subscription.State = SubscriptionState.Ready;
                                if (subscription.StateChangedCallback != null)
                                    await subscription.StateChangedCallback(subscription);
                            }
                        }
                    }
                    break;
            }
        }

        public Task Invoke(Func<Task> callback)
        {
            var source = new TaskCompletionSource<object>();
            _inQueue.Add(new InQueueEntry
            {
                Type = InQueueEntryType.Callback,
                Callback = async () =>
                {
                    await callback();
                    source.SetResult(null);
                }
            });
            return source.Task;
        }

        private string GenerateCallId()
        {
            return (_nextId++).ToString();
        }

        private async Task Call(string method, object[] values, Action<ErrorOrResult<dynamic>> callback)
        {
            var id = GenerateCallId();

            if (callback != null)
            {
                _pendingCalls.Add(id, new PendingCall
                {
                    Method = method,
                    Values = values,
                    Callback = callback
                });
            }

            if (values == null)
                values = new object[] { };

            await SendMessage(new
            {
                msg = "method",
                method = method,
                @params = values,
                id = id
            });
        }

        public async Task<ErrorOrResult<dynamic>> Call(string method, params object[] values)
        {
            var source = new TaskCompletionSource<ErrorOrResult<dynamic>>();
            await this.Call(method, values, eor => source.SetResult(eor));
            return await source.Task;
        }

        private string GenerateSubscriptionId()
        {
            var c = new char[10];

            for (int i = 0; i < c.Length; i++)
                c[i] = SubscriptionIdAlphabet[_random.Next(SubscriptionIdAlphabet.Length)];

            return new string(c);
        }

        public async Task<Subscription> Subscribe(string name, object[] values, Func<Subscription, Task> stateChangedCallback)
        {
            var id = GenerateSubscriptionId();
            var subscription = new Subscription(this, id);

            subscription.StateChangedCallback = stateChangedCallback;
            _subscriptions.Add(id, subscription);

            if (values == null)
                values = new object[] { };

            await SendMessage(new
            {
                msg = "sub",
                id = id,
                name = name,
                @params = values
            });

            return subscription;
        }

        public async Task<ErrorOrResult<Subscription>> Subscribe(string name, params object[] values)
        {
            var source = new TaskCompletionSource<ErrorOrResult<Subscription>>();
            await this.Subscribe(name, values, subscription =>
            {
                if (subscription.State == SubscriptionState.Ready)
                    source.SetResult(new ErrorOrResult<Subscription> { Result = subscription });
                else if (subscription.State == SubscriptionState.Unsubscribed)
                    source.SetResult(new ErrorOrResult<Subscription> { Error = subscription.Error });
                return TaskConstants.Completed;
            });
            return await source.Task;
        }

        private async Task Unsubscribe(string id)
        {
            await SendMessage(new
            {
                msg = "unsub",
                id = id
            });
        }

        public IReadOnlyDictionary<string, Collection> Collections { get { return _collections; } }

        public Collection RegisterCollection(string collectionName)
        {
            var collection = new Collection(this, collectionName);
            _collections.Add(collectionName, collection);
            return collection;
        }

        private void UnregisterCollection(string collectionName)
        {
            _collections.Remove(collectionName);
        }
    }
}
