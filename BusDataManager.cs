using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Dapper;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using Newtonsoft;
using Newtonsoft.Json;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using StackExchange.Redis;

namespace CatchTheBus
{
    public class BusDataManager
    {
        public class ActivatedGeoFence
        {
            public int BusDataId { get; set; }
            public int VehicleId { get; set; }
            public int DirectionId { get; set; }
            public int RouteId { get; set; }
            public string RouteName { get; set; }
            public int GeoFenceId { get; set; }
    		public string GeoFenceName { get; set; }          
		    public string GeoFenceStatus { get; set; }
            public DateTime TimestampUTC { get; set; }
        }

        private readonly string _connectionString = Environment.GetEnvironmentVariable("AzureSQLConnectionString");
        private readonly string _busRealTimeFeedUrl = Environment.GetEnvironmentVariable("RealTimeFeedUrl");
        private readonly string _IFTTTUrl = Environment.GetEnvironmentVariable("IFTTTUrl");

        private readonly ILogger _log;
        private readonly HttpClient _client = new HttpClient();

        //public RedisConnectionManager redisConnection = new RedisConnectionManager();

        public BusDataManager(ILogger log)
        {
            _log = log;
        }

        public async Task ProcessBusData()
        {
            //Test Redis connection:
            RedisConnectionManager.TestRedisConn();

            var feedTask = DownloadBusData();
            var monitoredRoutesTask = GetMonitoredRoutes();

            Task.WaitAll(new Task[] { feedTask, monitoredRoutesTask });

            var feed = await feedTask;
            var monitoredRoutes = await monitoredRoutesTask;

            // Find all the bus to be monitored
            var buses = feed.Entities.FindAll(e => monitoredRoutes.Contains(e.Vehicle.Trip.RouteId));

            _log.LogInformation($"Found {buses.Count} buses in monitored routes");

            await ProcessGeoFences(buses);
        }

        private async Task<GTFS.RealTime.Feed> DownloadBusData()
        {
           
            var response = await _client.GetAsync(_busRealTimeFeedUrl);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var feed = JsonConvert.DeserializeObject<GTFS.RealTime.Feed>(responseString);

            return feed;
        }

        private async Task<List<int>> GetMonitoredRoutes()
        {
           
            using var conn = new SqlConnection(_connectionString);
            var result = await conn.QueryAsync<int>("web.GetMonitoredRoutes", commandType: CommandType.StoredProcedure);
            return result.ToList();
        }

        private async Task ProcessGeoFences(List<GTFS.RealTime.Entity> buses)
        {
            // Build payload
            var busData = new JArray();
            buses.ForEach(b =>
            {
                //_log.LogInformation($"logging feed information {b.Vehicle.VehicleId.Id}: {b.Vehicle.Position.Latitude}, {b.Vehicle.Position.Longitude}");
                var d = new JObject
                {
                    ["DirectionId"] = b.Vehicle.Trip.DirectionId,
                    ["RouteId"] = b.Vehicle.Trip.RouteId,
                    ["VehicleId"] = b.Vehicle.VehicleId.Id,
                    ["Position"] = new JObject
                    {
                        ["Latitude"] = b.Vehicle.Position.Latitude,
                        ["Longitude"] = b.Vehicle.Position.Longitude
                    },
                    ["TimestampUTC"] = Utils.FromPosixTime(b.Vehicle.Timestamp)
                };

                busData.Add(d);
            });

            foreach (GTFS.RealTime.Entity bus in buses)
            {
                string serializedBus = System.Text.Json.JsonSerializer.Serialize(bus);
                bool stringSetResult = await RedisConnectionManager.cache.StringSetAsync(bus.Id, serializedBus);
                //Console.WriteLine($"Cache response from storing serialized Employee object: {stringSetResult}");
            }

            using var conn = new SqlConnection(_connectionString);
            {
                //write to cache
                var geoFences = await conn.QueryAsync<ActivatedGeoFence>("web.AddBusData", new { payload = busData.ToString() },  commandType: CommandType.StoredProcedure);
                _log.LogInformation($"Found {geoFences.Count()} activity on GeoFences");

                foreach (var gf in geoFences)
                {
                    _log.LogInformation($"Vehicle {gf.VehicleId}, route {gf.RouteName}, {gf.GeoFenceStatus} GeoFence {gf.GeoFenceName} at {gf.TimestampUTC} UTC");
                    await TriggerIFTTT(gf);
                }

               await  GetMonitoredBusData();
            }            
        }

        public async Task TriggerIFTTT(ActivatedGeoFence geoFence)
        {
            var content = JObject.Parse("{" + $"'value1':'{geoFence.VehicleId}', 'value2': '{geoFence.GeoFenceStatus}'" + "}");

            _log.LogInformation($"Calling IFTTT webhook for {geoFence.VehicleId}");
            //_log.LogDebug("POST: " + content.ToString());
            var stringContent = new StringContent(JsonConvert.SerializeObject(content, Formatting.None), Encoding.UTF8, "application/json");
            var iftttResult = await _client.PostAsync(_IFTTTUrl, stringContent);

            iftttResult.EnsureSuccessStatusCode();

            _log.LogInformation($"[{geoFence.VehicleId}/{geoFence.DirectionId}/{geoFence.GeoFenceId}] WebHook called successfully");
        }    

        public async Task<JObject> GetMonitoredBusData()
        {
            using(var conn = new SqlConnection(_connectionString))
            {
                var result = await conn.QuerySingleOrDefaultAsync<string>("web.GetMonitoredBusData", commandType: CommandType.StoredProcedure);
                String timeStamp = DateTime.UtcNow.ToString();
                _log.LogInformation($"{timeStamp}  {result}");

                bool stringSetResult = await RedisConnectionManager.cache.StringSetAsync("latestGeoFence "+ timeStamp, result);
                RedisKey rkey = new RedisKey("latestGeoFence");
                RedisValue rvalue = new RedisValue(result);
                StackExchange.Redis.CommandFlags flag = StackExchange.Redis.CommandFlags.None; 
                long liatSetResult = await RedisConnectionManager.cache.ListLeftPushAsync(rkey, rvalue, When.Always, flag);

                var busData = new {
                    geometry = result ?? string.Empty
                };

                return JObject.FromObject(busData);
            }
        }

        public async Task<JObject> GetMonitoredBusDataFromRedis()
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                //var result = await conn.QuerySingleOrDefaultAsync<string>("web.GetMonitoredBusData", commandType: CommandType.StoredProcedure);
                String timeStamp = DateTime.UtcNow.ToString();
                RedisKey rkey = new RedisKey("latestGeoFence");
                string stringGetResult = await RedisConnectionManager.cache.ListGetByIndexAsync(rkey, 0);

                _log.LogInformation($"{timeStamp}  {stringGetResult}");
                
               

                var busData = new
                {
                    geometry = stringGetResult ?? string.Empty
                };

                return JObject.FromObject(busData);
            }
        }
    }
}
