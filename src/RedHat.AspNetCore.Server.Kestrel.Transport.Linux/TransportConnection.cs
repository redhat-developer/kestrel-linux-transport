// Copied from https://github.com/aspnet/AspNetCore/tree/e0e9096af526448cb1a02f97e60efd8567b7ba35

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal abstract partial class TransportConnection : ConnectionContext
    {
        private IDictionary<object, object> _items;
        private string _connectionId;

        public TransportConnection()
        {
            FastReset();
        }

        public override EndPoint LocalEndPoint { get; set; }
        public override EndPoint RemoteEndPoint { get; set; }

        public override string ConnectionId
        {
            get
            {
                if (_connectionId == null)
                {
                    _connectionId = CorrelationIdGenerator.GetNextId();
                }

                return _connectionId;
            }
            set
            {
                _connectionId = value;
            }
        }

        public override IFeatureCollection Features => this;

        public virtual MemoryPool<byte> MemoryPool { get; }

        public override IDuplexPipe Transport { get; set; }

        public IDuplexPipe Application { get; set; }

        public override IDictionary<object, object> Items
        {
            get
            {
                // Lazily allocate connection metadata
                return _items ?? (_items = new ConnectionItems());
            }
            set
            {
                _items = value;
            }
        }

        public override CancellationToken ConnectionClosed { get; set; }

        // DO NOT remove this override to ConnectionContext.Abort. Doing so would cause
        // any TransportConnection that does not override Abort or calls base.Abort
        // to stack overflow when IConnectionLifetimeFeature.Abort() is called.
        // That said, all derived types should override this method should override
        // this implementation of Abort because canceling pending output reads is not
        // sufficient to abort the connection if there is backpressure.
        public override void Abort(ConnectionAbortedException abortReason)
        {
            Application.Input.CancelPendingRead();
        }
    }

    internal partial class TransportConnection : IConnectionIdFeature,
                                                 IConnectionTransportFeature,
                                                 IConnectionItemsFeature,
                                                 IMemoryPoolFeature,
                                                 IConnectionLifetimeFeature
    {
        // NOTE: When feature interfaces are added to or removed from this TransportConnection class implementation,
        // then the list of `features` in the generated code project MUST also be updated.
        // See also: tools/CodeGenerator/TransportConnectionFeatureCollection.cs

        MemoryPool<byte> IMemoryPoolFeature.MemoryPool => MemoryPool;

        IDuplexPipe IConnectionTransportFeature.Transport
        {
            get => Transport;
            set => Transport = value;
        }

        IDictionary<object, object> IConnectionItemsFeature.Items
        {
            get => Items;
            set => Items = value;
        }

        CancellationToken IConnectionLifetimeFeature.ConnectionClosed
        {
            get => ConnectionClosed;
            set => ConnectionClosed = value;
        }

        void IConnectionLifetimeFeature.Abort() => Abort(new ConnectionAbortedException("The connection was aborted by the application via IConnectionLifetimeFeature.Abort()."));
    }

    internal partial class TransportConnection : IFeatureCollection
    {
        private static readonly Type IConnectionIdFeatureType = typeof(IConnectionIdFeature);
        private static readonly Type IConnectionTransportFeatureType = typeof(IConnectionTransportFeature);
        private static readonly Type IConnectionItemsFeatureType = typeof(IConnectionItemsFeature);
        private static readonly Type IMemoryPoolFeatureType = typeof(IMemoryPoolFeature);
        private static readonly Type IConnectionLifetimeFeatureType = typeof(IConnectionLifetimeFeature);

        private object _currentIConnectionIdFeature;
        private object _currentIConnectionTransportFeature;
        private object _currentIConnectionItemsFeature;
        private object _currentIMemoryPoolFeature;
        private object _currentIConnectionLifetimeFeature;

        private int _featureRevision;

        private List<KeyValuePair<Type, object>> MaybeExtra;

        private void FastReset()
        {
            _currentIConnectionIdFeature = this;
            _currentIConnectionTransportFeature = this;
            _currentIConnectionItemsFeature = this;
            _currentIMemoryPoolFeature = this;
            _currentIConnectionLifetimeFeature = this;

        }

        // Internal for testing
        internal void ResetFeatureCollection()
        {
            FastReset();
            MaybeExtra?.Clear();
            _featureRevision++;
        }

        private object ExtraFeatureGet(Type key)
        {
            if (MaybeExtra == null)
            {
                return null;
            }
            for (var i = 0; i < MaybeExtra.Count; i++)
            {
                var kv = MaybeExtra[i];
                if (kv.Key == key)
                {
                    return kv.Value;
                }
            }
            return null;
        }

        private void ExtraFeatureSet(Type key, object value)
        {
            if (MaybeExtra == null)
            {
                MaybeExtra = new List<KeyValuePair<Type, object>>(2);
            }

            for (var i = 0; i < MaybeExtra.Count; i++)
            {
                if (MaybeExtra[i].Key == key)
                {
                    MaybeExtra[i] = new KeyValuePair<Type, object>(key, value);
                    return;
                }
            }
            MaybeExtra.Add(new KeyValuePair<Type, object>(key, value));
        }

        bool IFeatureCollection.IsReadOnly => false;

        int IFeatureCollection.Revision => _featureRevision;

        object IFeatureCollection.this[Type key]
        {
            get
            {
                object feature = null;
                if (key == IConnectionIdFeatureType)
                {
                    feature = _currentIConnectionIdFeature;
                }
                else if (key == IConnectionTransportFeatureType)
                {
                    feature = _currentIConnectionTransportFeature;
                }
                else if (key == IConnectionItemsFeatureType)
                {
                    feature = _currentIConnectionItemsFeature;
                }
                else if (key == IMemoryPoolFeatureType)
                {
                    feature = _currentIMemoryPoolFeature;
                }
                else if (key == IConnectionLifetimeFeatureType)
                {
                    feature = _currentIConnectionLifetimeFeature;
                }
                else if (MaybeExtra != null)
                {
                    feature = ExtraFeatureGet(key);
                }

                return feature;
            }

            set
            {
                _featureRevision++;

                if (key == IConnectionIdFeatureType)
                {
                    _currentIConnectionIdFeature = value;
                }
                else if (key == IConnectionTransportFeatureType)
                {
                    _currentIConnectionTransportFeature = value;
                }
                else if (key == IConnectionItemsFeatureType)
                {
                    _currentIConnectionItemsFeature = value;
                }
                else if (key == IMemoryPoolFeatureType)
                {
                    _currentIMemoryPoolFeature = value;
                }
                else if (key == IConnectionLifetimeFeatureType)
                {
                    _currentIConnectionLifetimeFeature = value;
                }
                else
                {
                    ExtraFeatureSet(key, value);
                }
            }
        }

        TFeature IFeatureCollection.Get<TFeature>()
        {
            TFeature feature = default;
            if (typeof(TFeature) == typeof(IConnectionIdFeature))
            {
                feature = (TFeature)_currentIConnectionIdFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionTransportFeature))
            {
                feature = (TFeature)_currentIConnectionTransportFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionItemsFeature))
            {
                feature = (TFeature)_currentIConnectionItemsFeature;
            }
            else if (typeof(TFeature) == typeof(IMemoryPoolFeature))
            {
                feature = (TFeature)_currentIMemoryPoolFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeFeature))
            {
                feature = (TFeature)_currentIConnectionLifetimeFeature;
            }
            else if (MaybeExtra != null)
            {
                feature = (TFeature)(ExtraFeatureGet(typeof(TFeature)));
            }

            return feature;
        }

        void IFeatureCollection.Set<TFeature>(TFeature feature)
        {
            _featureRevision++;
            if (typeof(TFeature) == typeof(IConnectionIdFeature))
            {
                _currentIConnectionIdFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionTransportFeature))
            {
                _currentIConnectionTransportFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionItemsFeature))
            {
                _currentIConnectionItemsFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IMemoryPoolFeature))
            {
                _currentIMemoryPoolFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeFeature))
            {
                _currentIConnectionLifetimeFeature = feature;
            }
            else
            {
                ExtraFeatureSet(typeof(TFeature), feature);
            }
        }

        private IEnumerable<KeyValuePair<Type, object>> FastEnumerable()
        {
            if (_currentIConnectionIdFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionIdFeatureType, _currentIConnectionIdFeature);
            }
            if (_currentIConnectionTransportFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionTransportFeatureType, _currentIConnectionTransportFeature);
            }
            if (_currentIConnectionItemsFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionItemsFeatureType, _currentIConnectionItemsFeature);
            }
            if (_currentIMemoryPoolFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IMemoryPoolFeatureType, _currentIMemoryPoolFeature);
            }
            if (_currentIConnectionLifetimeFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionLifetimeFeatureType, _currentIConnectionLifetimeFeature);
            }

            if (MaybeExtra != null)
            {
                foreach (var item in MaybeExtra)
                {
                    yield return item;
                }
            }
        }

        IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator() => FastEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => FastEnumerable().GetEnumerator();
    }

    internal static class CorrelationIdGenerator
    {
        // Base32 encoding - in ascii sort order for easy text based sorting
        private static readonly char[] s_encode32Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUV".ToCharArray();

        // Seed the _lastConnectionId for this application instance with
        // the number of 100-nanosecond intervals that have elapsed since 12:00:00 midnight, January 1, 0001
        // for a roughly increasing _lastId over restarts
        private static long _lastId = DateTime.UtcNow.Ticks;

        public static string GetNextId() => GenerateId(Interlocked.Increment(ref _lastId));

        private static string GenerateId(long id)
        {
            return string.Create(13, id, (buffer, value) =>
            {
                char[] encode32Chars = s_encode32Chars;

                buffer[12] = encode32Chars[value & 31];
                buffer[11] = encode32Chars[(value >> 5) & 31];
                buffer[10] = encode32Chars[(value >> 10) & 31];
                buffer[9] = encode32Chars[(value >> 15) & 31];
                buffer[8] = encode32Chars[(value >> 20) & 31];
                buffer[7] = encode32Chars[(value >> 25) & 31];
                buffer[6] = encode32Chars[(value >> 30) & 31];
                buffer[5] = encode32Chars[(value >> 35) & 31];
                buffer[4] = encode32Chars[(value >> 40) & 31];
                buffer[3] = encode32Chars[(value >> 45) & 31];
                buffer[2] = encode32Chars[(value >> 50) & 31];
                buffer[1] = encode32Chars[(value >> 55) & 31];
                buffer[0] = encode32Chars[(value >> 60) & 31];
            });
        }
    }
}
