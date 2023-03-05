using System;
using System.Collections.Generic;
using System.Text;
using StackExchange.Redis;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CatchTheBus
{
    public class RedisConnectionManager
    {
        private static Lazy<ConnectionMultiplexer> lazyConnection = new Lazy<ConnectionMultiplexer>(() => GetNewMux().Result);

        private static async Task<ConnectionMultiplexer> GetNewMux()
        {
            string cacheUrl = Environment.GetEnvironmentVariable("RedisConnectionString");

            return ConnectionMultiplexer.Connect(cacheUrl);
        }

        public static ConnectionMultiplexer Connection
        {
            get
            {
                return lazyConnection.Value;
            }
        }

        public static IDatabase cache;

        public static void TestRedisConn()
        {
            try
            {
                var mux = Connection;
            }
            catch (RedisTimeoutException)
            {
                lazyConnection = new Lazy<ConnectionMultiplexer>(() => GetNewMux().Result);
            }

            cache = Connection.GetDatabase();

            // Perform cache operations using the cache object...

            // Simple PING command
            string cacheCommand = "PING";
            Console.WriteLine("\nCache command  : " + cacheCommand);
            Console.WriteLine("Cache response : " + cache.Execute(cacheCommand).ToString());
        }
    }
}
