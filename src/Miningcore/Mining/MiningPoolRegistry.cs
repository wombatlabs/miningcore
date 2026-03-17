using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;

namespace Miningcore.Mining
{
    public sealed class MiningPoolRegistry : IDisposable
    {
        private readonly ConcurrentDictionary<string, IMiningPool> pools =
            new ConcurrentDictionary<string, IMiningPool>(StringComparer.OrdinalIgnoreCase);

        private readonly IDisposable subscription;

        public MiningPoolRegistry(IMessageBus bus)
        {
            // Listen once for pools coming online
            subscription = bus.Listen<PoolStatusNotification>()
                .Where(n => n.Status == PoolStatus.Online && n.Pool != null && n.Pool.Config != null && n.Pool.Config.Id != null)
                .Subscribe(n =>
                {
                    pools[n.Pool.Config.Id] = n.Pool;
                });
        }

        public IMiningPool Get(string poolId)
        {
            IMiningPool pool;
            pools.TryGetValue(poolId, out pool);
            return pool;
        }

        public IEnumerable<IMiningPool> All
        {
            get { return pools.Values; }
        }

        public void Dispose()
        {
            if(subscription != null)
                subscription.Dispose();
        }
    }
}
