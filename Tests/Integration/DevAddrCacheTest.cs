// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.Tests.Integration
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using LoraKeysManagerFacade;
    using LoRaWan.Tests.Common;
    using Moq;
    using Newtonsoft.Json;
    using StackExchange.Redis;
    using Xunit;

    [Collection(RedisFixture.CollectionName)]
    public class DevAddrCacheTest : FunctionTestBase, IClassFixture<RedisFixture>
    {
        private const string FullUpdateKey = "fullUpdateKey";
        private const string GlobalDevAddrUpdateKey = "globalUpdateKey";
        private const string CacheKeyPrefix = "devAddrTable:";

        private const string PrimaryKey = "ABCDEFGH1234567890";
        private const string IotHubHostName = "fake.azure-devices.net";

        private readonly ILoRaDeviceCacheStore cache;

        public DevAddrCacheTest(RedisFixture redis)
        {
            if (redis is null) throw new ArgumentNullException(nameof(redis));
            this.cache = new LoRaDeviceCacheRedisStore(redis.Database);
        }

        private static Mock<IDeviceRegistryManager> InitRegistryManager(List<DevAddrCacheInfo> deviceIds, int numberOfDeviceDeltaUpdates = 2)
        {
            var currentDevAddrContext = new List<DevAddrCacheInfo>();
            var currentDevices = deviceIds;
            var mockRegistryManager = new Mock<IDeviceRegistryManager>(MockBehavior.Strict);
            var hasMoreShouldReturn = true;

            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            mockRegistryManager
                 .Setup(x => x.GetDeviceAsync(It.IsAny<string>()))
                 .ReturnsAsync((string deviceId) =>
                 {
                     var mockDevice = new Mock<IDevice>(MockBehavior.Strict);

                     mockDevice.SetupGet(t => t.PrimaryKey)
                         .Returns(primaryKey);

                     mockDevice.SetupGet(t => t.AssignedIoTHub)
                        .Returns(IotHubHostName);

                     return mockDevice.Object;
                 });

            mockRegistryManager
                .Setup(x => x.GetTwinAsync(It.IsNotNull<string>()))
                .ReturnsAsync((string deviceId) =>
                {
                    var mockDevice = new Mock<IDeviceTwin>(MockBehavior.Strict);

                    mockDevice.SetupGet(t => t.DeviceId)
                        .Returns(deviceId);
                    mockDevice.Setup(t => t.GetDevAddr())
                              .Returns(CreateDevAddr());
                    mockDevice.Setup(t => t.GetGatewayID())
                              .Returns(string.Empty);
                    mockDevice.Setup(t => t.GetLastUpdated())
                              .Returns(DateTime.UtcNow);
                    mockDevice.Setup(t => t.GetNwkSKey())
                              .Returns(string.Empty);

                    return mockDevice.Object;
                });

            // CacheMiss query
            var cacheMissQueryMock = new Mock<IRegistryPageResult<IDeviceTwin>>(MockBehavior.Strict);

            // we only want to run hasmoreresult once
            cacheMissQueryMock
                .Setup(x => x.HasMoreResults)
                .Returns(() =>
                {
                    if (hasMoreShouldReturn)
                    {
                        hasMoreShouldReturn = false;
                        return true;
                    }

                    return false;
                });

            cacheMissQueryMock
                .Setup(x => x.GetNextPageAsync())
                .ReturnsAsync(() =>
                {
                    var devAddressesToConsider = currentDevAddrContext;
                    var twins = new List<IDeviceTwin>();
                    foreach (var devaddrItem in devAddressesToConsider)
                    {
                        var mockDevice = new Mock<IDeviceTwin>(MockBehavior.Strict);
                        mockDevice.SetupGet(t => t.DeviceId).Returns(devaddrItem.DevEUI);
                        mockDevice.Setup(t => t.GetGatewayID()).Returns(devaddrItem.GatewayId);
                        mockDevice.Setup(t => t.GetDevAddr()).Returns(devaddrItem.DevAddr);
                        mockDevice.Setup(t => t.GetNwkSKey()).Returns(devaddrItem.NwkSKey);
                        mockDevice.Setup(t => t.GetLastUpdated()).Returns(devaddrItem.LastUpdatedTwins);

                        twins.Add(mockDevice.Object);
                    }

                    return twins;
                });

            mockRegistryManager
                .Setup(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()))
                .ReturnsAsync((string devAddr) =>
                {
                    hasMoreShouldReturn = true;
                    currentDevAddrContext = currentDevices.Where(v => v.DevAddr.ToString() == devAddr.Split('\'')[1]).ToList();
                    return cacheMissQueryMock.Object;
                });

            mockRegistryManager
                .Setup(x => x.FindConfiguredLoRaDevices())
                .ReturnsAsync(() =>
                {
                    hasMoreShouldReturn = true;
                    currentDevAddrContext = currentDevices;
                    return cacheMissQueryMock.Object;
                });

            mockRegistryManager
                .Setup(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()))
                .ReturnsAsync((string query) =>
                {
                    currentDevAddrContext = currentDevices.Take(numberOfDeviceDeltaUpdates).ToList();
                    // reset device count in case HasMoreResult is called more than once
                    hasMoreShouldReturn = true;
                    return cacheMissQueryMock.Object;
                });
            return mockRegistryManager;
        }

        private static void InitCache(ILoRaDeviceCacheStore cache, List<DevAddrCacheInfo> deviceIds)
        {
            var loradevaddrcache = new LoRaDevAddrCache(cache, null, null);
            foreach (var device in deviceIds)
            {
                loradevaddrcache.StoreInfo(device);
            }
        }

        /// <summary>
        /// Ensure that the Locks get released if an exception pop.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(FullUpdateKey)]
        public async Task When_PerformNeededSyncs_Fails_Should_Release_Lock(string lockToTake)
        {
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake == null ? null : new[] { lockToTake });
            var managerInput = new List<DevAddrCacheInfo> { new DevAddrCacheInfo() { DevEUI = TestEui.GenerateDevEui(), DevAddr = CreateDevAddr() } };
            var registryManagerMock = InitRegistryManager(managerInput);

            registryManagerMock.Setup(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>())).Throws(new RedisException(string.Empty));
            registryManagerMock.Setup(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>())).Throws(new RedisException(string.Empty));
            registryManagerMock.Setup(x => x.FindConfiguredLoRaDevices()).Throws(new RedisException(string.Empty));

            var devAddrcache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, null);
            await devAddrcache.PerformNeededSyncs();

            // When doing a full update, the FullUpdateKey lock should be reset to 1min, the GlobalDevAddrUpdateKey should be gone
            // When doing a partial update, the GlobalDevAddrUpdateKey should be gone
            switch (lockToTake)
            {
                case FullUpdateKey:
                    Assert.Null(await this.cache.GetObjectTTL(GlobalDevAddrUpdateKey));
                    break;
                case null:
                    var nextFullUpdate = await this.cache.GetObjectTTL(FullUpdateKey);
                    Assert.True(nextFullUpdate <= TimeSpan.FromMinutes(1));
                    Assert.Null(await this.cache.GetObjectTTL(GlobalDevAddrUpdateKey));
                    break;
                default: throw new InvalidOperationException("invalid test case");
            }
        }

        [Fact]
        // This test simulate a new call from an unknow device. It checks that :
        // The server correctly query iot hub
        // Server saves answer in the Cache for future usage
        public async Task When_DevAddr_Is_Not_In_Cache_Query_Iot_Hub_And_Save_In_Cache()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var managerInput = new List<DevAddrCacheInfo>();

            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr()
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            var registryManagerMock = InitRegistryManager(managerInput);

            var items = new List<IoTHubDeviceInfo>();

            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            items = await deviceGetter.GetDeviceList(null, gatewayId, new DevNonce(0xABCD), devAddrJoining);

            Assert.Single(items);
            // If a cache miss it should save it in the redisCache
            var devAddrcache = new LoRaDevAddrCache(this.cache, null, null);
            var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(queryResult);
            var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
            Assert.Equal(managerInput[0].DevAddr, resultObject.DevAddr);
            Assert.Equal(managerInput[0].GatewayId ?? string.Empty, resultObject.GatewayId);
            Assert.Equal(managerInput[0].DevEUI, resultObject.DevEUI);

            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Once);
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Once);
        }

        [Fact]
        // This test simulate a call received by multiple server. It ensures IoT Hub is only queried once.
        public async Task Multi_Gateway_When_DevAddr_Is_Not_In_Cache_Query_Iot_Hub_Only_Once_And_Save_In_Cache()
        {
            var gateway1 = NewUniqueEUI64();
            var gateway2 = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var managerInput = new List<DevAddrCacheInfo>();

            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr()
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            var registryManagerMock = InitRegistryManager(managerInput);

            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            // Simulate three queries
            var tasks =
                from gw in new[] { gateway1, gateway2 }
                select Enumerable.Repeat(gw, 2) into gws // repeat each gateway twice
                from gw in gws
                select deviceGetter.GetDeviceList(null, gw, new DevNonce(0xABCD), devAddrJoining);

            await Task.WhenAll(tasks);

            // If a cache miss it should save it in the redisCache
            var devAddrcache = new LoRaDevAddrCache(this.cache, null, null);
            var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(queryResult);
            var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
            Assert.Equal(managerInput[0].DevAddr, resultObject.DevAddr);
            Assert.Equal(managerInput[0].GatewayId ?? string.Empty, resultObject.GatewayId);
            Assert.Equal(managerInput[0].DevEUI, resultObject.DevEUI);

            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Never);
            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);

            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Once);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Once);
        }

        [Fact]
        // This test ensure that if a device is in cache without a key, it get the keys from iot hub and saave it
        public async Task When_DevAddr_Is_In_Cache_Without_Key_Should_Not_Query_Iot_Hub_For_Twin_But_Should_Get_Key_And_Update()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            InitCache(this.cache, managerInput);
            var registryManagerMock = InitRegistryManager(managerInput);
            var items = new List<IoTHubDeviceInfo>();

            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            items = await deviceGetter.GetDeviceList(null, gatewayId, new DevNonce(0xABCD), devAddrJoining);

            Assert.Single(items);
            var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(queryResult);
            // The key should have been saved
            var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
            Assert.NotNull(resultObject.PrimaryKey);

            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");

            // Should query for the key as key is missing
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Once);
        }

        [Fact]
        // This test ensure that if a device is in cache without a key, it get the keys from iot hub and save it
        public async Task Multi_Gateway_When_DevAddr_Is_In_Cache_Without_Key_Should_Not_Query_Iot_Hub_For_Twin_But_Should_Get_Key_And_Update()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            InitCache(this.cache, managerInput);
            var registryManagerMock = InitRegistryManager(managerInput);

            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            var tasks =
                from gw in Enumerable.Repeat(gatewayId, 3)
                select deviceGetter.GetDeviceList(null, gw, new DevNonce(0xABCD), devAddrJoining);

            await Task.WhenAll(tasks);
            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            // Should query for the key as key is missing
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Once);
            var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(queryResult);
            // The key should have been saved
            var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
            Assert.NotNull(resultObject.PrimaryKey);
        }

        [Fact]
        // This test ensure that if the device has the key within the cache, it should not make any query to iot hub
        public async Task When_DevAddr_Is_In_Cache_With_Key_Should_Not_Query_Iot_Hub_For_Twin_At_All()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                    PrimaryKey = primaryKey,
                    LastUpdatedTwins = dateTime
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            InitCache(this.cache, managerInput);
            var registryManagerMock = InitRegistryManager(managerInput);

            var items = new List<IoTHubDeviceInfo>();
            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            items = await deviceGetter.GetDeviceList(null, gatewayId, new DevNonce(0xABCD), devAddrJoining);

            Assert.Single(items);
            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            // Should not query for the key as key is there
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        // This test ensure that if the device has the key within the cache, it should not make any query to iot hub
        public async Task When_Device_Is_Not_Ours_Save_In_Cache_And_Dont_Query_Hub_Again()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            // In this test we want no updates running
            // initialize locks for test to run correctly
            var lockToTake = new string[2] { FullUpdateKey, GlobalDevAddrUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, lockToTake);

            var items = new List<IoTHubDeviceInfo>();
            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 2; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                    PrimaryKey = primaryKey,
                    LastUpdatedTwins = dateTime
                });
            }

            var devAddrJoining = CreateDevAddr();
            InitCache(this.cache, managerInput);
            var registryManagerMock = InitRegistryManager(managerInput);

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            items = await deviceGetter.GetDeviceList(null, gatewayId, new DevNonce(0xABCD), devAddrJoining);

            Assert.Empty(items);
            var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(queryResult);
            var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
            Assert.Null(resultObject.DevEUI);
            Assert.Null(resultObject.PrimaryKey);
            Assert.Null(resultObject.GatewayId);
            var query2Result = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, devAddrJoining));
            Assert.Single(query2Result);

            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.Is((DevAddr x) => managerInput.Any(c => c.DevAddr == x))), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            registryManagerMock.Verify(x => x.GetTwinAsync(It.Is((string x) => managerInput.Any(c => c.DevEUI == x))), Times.Never, "IoT Hub should not have been called, as the device was present in the cache.");
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.Is((string x) => managerInput.Any(c => c.DevEUI == x))), Times.Never);
        }

        [Fact]
        // Check that the server perform a full reload if the locking key for full reload is not present
        public async Task When_FullUpdateKey_Is_Not_there_Should_Perform_Full_Reload()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            // The cache start as empty
            var registryManagerMock = InitRegistryManager(managerInput);

            // initialize locks for test to run correctly
            await LockDevAddrHelper.PrepareLocksForTests(this.cache);

            var items = new List<IoTHubDeviceInfo>();

            var deviceGetter = new DeviceGetter(registryManagerMock.Object, this.cache);
            items = await deviceGetter.GetDeviceList(null, gatewayId, new DevNonce(0xABCD), devAddrJoining);

            Assert.Single(items);
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Once);
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);

            // We expect to query for the key once (the device with an active connection)
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Once);

            // we expect the devices are saved
            for (var i = 1; i < 5; i++)
            {
                var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, managerInput[i].DevAddr));
                Assert.Single(queryResult);
                var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                Assert.Equal(managerInput[i].GatewayId, resultObject.GatewayId);
                Assert.Equal(managerInput[i].DevEUI, resultObject.DevEUI);
            }
        }

        [Fact]
        // Trigger delta update correctly to see if it performs correctly on an empty cache
        public async Task Delta_Update_Perform_Correctly_On_Empty_Cache()
        {
            var gatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;

            var managerInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = gatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            var devAddrJoining = managerInput[0].DevAddr;
            // The cache start as empty
            var registryManagerMock = InitRegistryManager(managerInput);

            // initialize locks for test to run correctly
            var locksToTake = new string[1] { FullUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, locksToTake);

            var devAddrCache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, gatewayId);
            await devAddrCache.PerformNeededSyncs();

            while (!string.IsNullOrEmpty(this.cache.StringGet(GlobalDevAddrUpdateKey)))
            {
                await Task.Delay(100);
            }

            var foundItem = 0;
            // we expect the devices are saved
            for (var i = 0; i < 5; i++)
            {
                var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, managerInput[i].DevAddr));
                if (queryResult.Length > 0)
                {
                    foundItem++;
                    Assert.Single(queryResult);
                    var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                    Assert.Equal(managerInput[i].GatewayId, resultObject.GatewayId);
                    Assert.Equal(managerInput[i].DevEUI, resultObject.DevEUI);
                }
            }

            // Only two items should be updated by the delta updates
            Assert.Equal(2, foundItem);

            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Never);
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never);
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);

            // We expect to query for the key once (the device with an active connection)
            // The first time during the LoRaDevAddrCache constructor
            // The second time during the delta synchronization
            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        // This test perform a delta update and we check the following
        // primary key present in the cache is still here after a delta up
        // Items with save Devaddr are correctly saved (one old from cache, one from iot hub)
        // Gateway Id is correctly updated in old cache information.
        // Primary Key are kept as UpdateTime is similar
        public async Task Delta_Update_Perform_Correctly_On_Non_Empty_Cache_And_Keep_Old_Values()
        {
            var oldGatewayId = NewUniqueEUI64();
            var newGatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;

            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var managerInput = new List<DevAddrCacheInfo>();

            var adressForDuplicateDevAddr = CreateDevAddr();
            for (var i = 0; i < 5; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = newGatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            managerInput.Add(new DevAddrCacheInfo()
            {
                DevEUI = TestEui.GenerateDevEui(),
                DevAddr = adressForDuplicateDevAddr,
                GatewayId = newGatewayId,
                LastUpdatedTwins = dateTime
            });

            var devAddrJoining = managerInput[0].DevAddr;
            // The cache start as empty
            var registryManagerMock = InitRegistryManager(managerInput, managerInput.Count);

            // Set up the cache with expectation.
            var cacheInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                cacheInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = managerInput[i].DevEUI,
                    DevAddr = managerInput[i].DevAddr,
                    GatewayId = oldGatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            cacheInput[2].PrimaryKey = primaryKey;
            cacheInput[3].PrimaryKey = primaryKey;

            var devEui = TestEui.GenerateDevEui();
            cacheInput.Add(new DevAddrCacheInfo()
            {
                DevEUI = devEui,
                DevAddr = adressForDuplicateDevAddr,
                GatewayId = oldGatewayId,
                PrimaryKey = primaryKey,
                LastUpdatedTwins = dateTime
            });
            InitCache(this.cache, cacheInput);

            // initialize locks for test to run correctly
            var locksToTake = new string[1] { FullUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, locksToTake);

            var devAddrCache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, newGatewayId);
            await devAddrCache.PerformNeededSyncs();

            // we expect the devices are saved
            for (var i = 0; i < managerInput.Count; i++)
            {
                if (managerInput[i].DevAddr != adressForDuplicateDevAddr)
                {
                    var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, managerInput[i].DevAddr));
                    Assert.Single(queryResult);
                    var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                    Assert.Equal(managerInput[i].GatewayId, resultObject.GatewayId);
                    Assert.Equal(managerInput[i].DevEUI, resultObject.DevEUI);
                    Assert.Equal(cacheInput[i].PrimaryKey, resultObject.PrimaryKey);
                }
            }

            // let's check the devices with a double EUI
            var query2Result = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, adressForDuplicateDevAddr));
            Assert.Equal(2, query2Result.Length);
            for (var i = 0; i < 2; i++)
            {
                var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(query2Result[0].Value);
                if (resultObject.DevEUI == devEui)
                {
                    Assert.Equal(oldGatewayId, resultObject.GatewayId);
                    Assert.Equal(primaryKey, resultObject.PrimaryKey);
                }
                else
                {
                    Assert.Equal(newGatewayId, resultObject.GatewayId);
                    Assert.True(string.IsNullOrEmpty(resultObject.PrimaryKey));
                }
            }

            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never);
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Never);

            // The first time during the LoRaDevAddrCache constructor
            // The second time during the delta synchronization
            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        // This test perform a delta update and we check the following
        // primary key present in the cache is still here after a delta up
        // Items with save Devaddr are correctly saved (one old from cache, one from iot hub)
        // Gateway Id is correctly updated in old cache information.
        // Primary Key are dropped as updatetime is defferent
        public async Task Delta_Update_Perform_Correctly_On_Non_Empty_Cache_And_Keep_Old_Values_Except_Primary_Key()
        {
            var oldGatewayId = NewUniqueEUI64();
            var newGatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var updateDateTime = DateTime.UtcNow.AddMinutes(10);

            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var managerInput = new List<DevAddrCacheInfo>();

            for (var i = 0; i < 5; i++)
            {
                managerInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = newGatewayId,
                    LastUpdatedTwins = updateDateTime
                });
            }

            var registryManagerMock = InitRegistryManager(managerInput, managerInput.Count);

            // Set up the cache with expectation.
            var cacheInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                cacheInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = managerInput[i].DevEUI,
                    DevAddr = managerInput[i].DevAddr,
                    LastUpdatedTwins = dateTime,
                    PrimaryKey = primaryKey
                });
            }

            InitCache(this.cache, cacheInput);
            // initialize locks for test to run correctly
            var locksToTake = new string[1] { FullUpdateKey };
            await LockDevAddrHelper.PrepareLocksForTests(this.cache, locksToTake);

            var devAddrCache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, newGatewayId);
            await devAddrCache.PerformNeededSyncs();

            // we expect the devices are saved
            for (var i = 0; i < managerInput.Count; i++)
            {
                var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, managerInput[i].DevAddr));
                Assert.Single(queryResult);
                var resultObject = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                Assert.Equal(managerInput[i].GatewayId, resultObject.GatewayId);
                Assert.Equal(managerInput[i].DevEUI, resultObject.DevEUI);
                // as the object changed the keys should not be saved
                Assert.Equal(string.Empty, resultObject.PrimaryKey);
            }

            // Iot hub should never have been called.
            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never);
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Never);

            // We expect to query for the key once (the device with an active connection)
            // The first time during the LoRaDevAddrCache constructor
            // The second time during the delta synchronization
            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        // This test perform a full update and we check the following
        // primary key present in the cache is still here after a fullupdate
        // Items with same Devaddr are correctly saved (one old from cache, one from iot hub)
        // Old cache items sharing a devaddr not in the new update are correctly removed
        // Items with a devAddr not in the update are correctly still in cache
        // Gateway Id is correctly updated in old cache information.
        // Primary Key are kept as UpdateTime is similar
        public async Task Full_Update_Perform_Correctly_On_Non_Empty_Cache_And_Keep_Old_Values()
        {
            var oldGatewayId = NewUniqueEUI64();
            var newGatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var newValues = new List<DevAddrCacheInfo>();

            var adressForDuplicateDevAddr = CreateDevAddr();
            for (var i = 0; i < 5; i++)
            {
                newValues.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = newGatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            newValues.Add(new DevAddrCacheInfo()
            {
                DevEUI = TestEui.GenerateDevEui(),
                DevAddr = adressForDuplicateDevAddr,
                GatewayId = newGatewayId,
                LastUpdatedTwins = dateTime
            });

            var devAddrJoining = newValues[0].DevAddr;
            // The cache start as empty
            var registryManagerMock = InitRegistryManager(newValues, newValues.Count);

            // Set up the cache with expectation.
            var cacheInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                cacheInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = newValues[i].DevEUI,
                    DevAddr = newValues[i].DevAddr,
                    GatewayId = oldGatewayId,
                    LastUpdatedTwins = dateTime
                });
            }

            cacheInput[2].PrimaryKey = primaryKey;
            cacheInput[3].PrimaryKey = primaryKey;

            // this is a device that will be overwritten by the update as it share a devaddr with an updated device
            var devEuiDoubleItem = TestEui.GenerateDevEui();

            cacheInput.Add(new DevAddrCacheInfo()
            {
                DevEUI = devEuiDoubleItem,
                DevAddr = adressForDuplicateDevAddr,
                GatewayId = oldGatewayId,
                PrimaryKey = primaryKey,
                LastUpdatedTwins = dateTime
            });

            InitCache(this.cache, cacheInput);

            // initialize locks for test to run correctly
            await LockDevAddrHelper.PrepareLocksForTests(this.cache);

            var devAddrCache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, newGatewayId);
            await devAddrCache.PerformNeededSyncs();

            // we expect the devices are saved, the double device id should not be there anymore
            for (var i = 0; i < newValues.Count; i++)
            {
                var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, newValues[i].DevAddr));
                Assert.Single(queryResult);
                var result2Object = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                Assert.Equal(newGatewayId, result2Object.GatewayId);
                Assert.Equal(newValues[i].DevEUI, result2Object.DevEUI);
                if (newValues[i].DevEUI == devEuiDoubleItem)
                {
                    Assert.Equal(cacheInput[i].PrimaryKey, result2Object.PrimaryKey);
                }
            }

            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never);

            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Once);
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Once);
        }

        [Fact]
        // This test perform a full update and we check the following
        // primary key present in the cache is still here after a fullupdate
        // Items with same Devaddr are correctly saved (one old from cache, one from iot hub)
        // Old cache items sharing a devaddr not in the new update are correctly removed
        // Items with a devAddr not in the update are correctly still in cache
        // Gateway Id is correctly updated in old cache information.
        // Primary Key are not kept as UpdateTime is not similar
        public async Task Full_Update_Perform_Correctly_On_Non_Empty_Cache_And_Keep_Old_Values_Except_Primary_Keys()
        {
            var oldGatewayId = NewUniqueEUI64();
            var newGatewayId = NewUniqueEUI64();
            var dateTime = DateTime.UtcNow;
            var updateDateTime = DateTime.UtcNow.AddMinutes(3);
            var primaryKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrimaryKey));
            var newValues = new List<DevAddrCacheInfo>();

            for (var i = 0; i < 5; i++)
            {
                newValues.Add(new DevAddrCacheInfo()
                {
                    DevEUI = TestEui.GenerateDevEui(),
                    DevAddr = CreateDevAddr(),
                    GatewayId = newGatewayId,
                    PrimaryKey = string.Empty,
                    LastUpdatedTwins = updateDateTime
                });
            }

            // The cache start as empty
            var registryManagerMock = InitRegistryManager(newValues, newValues.Count);

            // Set up the cache with expectation.
            var cacheInput = new List<DevAddrCacheInfo>();
            for (var i = 0; i < 5; i++)
            {
                cacheInput.Add(new DevAddrCacheInfo()
                {
                    DevEUI = newValues[i].DevEUI,
                    DevAddr = newValues[i].DevAddr,
                    GatewayId = oldGatewayId,
                    LastUpdatedTwins = dateTime,
                    PrimaryKey = primaryKey
                });
            }

            InitCache(this.cache, cacheInput);

            // initialize locks for test to run correctly
            await LockDevAddrHelper.PrepareLocksForTests(this.cache);

            var devAddrCache = new LoRaDevAddrCache(this.cache, registryManagerMock.Object, null, newGatewayId);
            await devAddrCache.PerformNeededSyncs();

            // we expect the devices are saved, the double device id should not be there anymore
            for (var i = 0; i < newValues.Count; i++)
            {
                var queryResult = this.cache.GetHashObject(string.Concat(CacheKeyPrefix, newValues[i].DevAddr));
                Assert.Single(queryResult);
                var result2Object = JsonConvert.DeserializeObject<DevAddrCacheInfo>(queryResult[0].Value);
                Assert.Equal(newGatewayId, result2Object.GatewayId);
                Assert.Equal(newValues[i].DevEUI, result2Object.DevEUI);
                Assert.Null(result2Object.PrimaryKey);
            }

            registryManagerMock.Verify(x => x.GetTwinAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.GetDeviceAsync(It.IsAny<string>()), Times.Never);
            registryManagerMock.Verify(x => x.FindDeviceByAddrAsync(It.IsAny<DevAddr>()), Times.Never);

            registryManagerMock.Verify(x => x.FindDevicesByLastUpdateDate(It.IsAny<string>()), Times.Once);
            registryManagerMock.Verify(x => x.FindConfiguredLoRaDevices(), Times.Once);
        }

        private static DevAddr CreateDevAddr() => new DevAddr((uint)RandomNumberGenerator.GetInt32(int.MaxValue));
    }
}
