// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.Tests.Common
{
    using LoRaWan.NetworkServer;

    /// <summary>
    /// Helper <see cref="ILoRaDeviceClientConnectionManager"/> implementation for unit tests.
    /// </summary>
    public sealed class SingleDeviceConnectionManager : ILoRaDeviceClientConnectionManager
    {
        private readonly ILoRaDeviceClient singleDeviceClient;

        public SingleDeviceConnectionManager(ILoRaDeviceClient deviceClient)
        {
            this.singleDeviceClient = deviceClient;
        }

        public bool EnsureConnected(LoRaDevice loRaDevice) => true;

        public ILoRaDeviceClient GetClient(LoRaDevice loRaDevice) => this.singleDeviceClient;

        public void Register(LoRaDevice loRaDevice, ILoRaDeviceClient loraDeviceClient)
        {
        }

        public void Release(LoRaDevice loRaDevice)
        {
            this.singleDeviceClient.Dispose();
        }

        public void TryScanExpiredItems()
        {
        }

        public void Dispose() => this.singleDeviceClient.Dispose();

        public ILoRaDeviceClient GetClient(string devEUI)
        {
            return this.singleDeviceClient;
        }

        public void Release(string devEUI)
        {
            this.singleDeviceClient.Dispose();
        }
    }
}