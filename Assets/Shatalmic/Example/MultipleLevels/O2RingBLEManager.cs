using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;

public class O2RingBLEManager : MonoBehaviour
{

    public int o2;
    public int bpm;
    
    public TextMeshProUGUI o2Text;
    public TextMeshProUGUI bpmText;
    
    
    // Constants for O2Ring BLE communication
    public static class O2Constants
    {
        public const string BLE_SERVICE_UUID = "14839ac4-7d7e-415c-9a42-167340cf2339";
        public const string BLE_READ_UUID = "0734594a-a8e7-4b1a-a6b1-cd5243059a57";
        public const string BLE_WRITE_UUID = "8b00ace7-eb0b-49b0-bbe9-9aee0a26e1a3";
        public const byte CMD_INFO = 20; // 0x14
        public const byte CMD_READ_SENSORS = 23; // 0x17
    }

    // Packet handling class for O2Ring communication
    public class O2Packet
    {
        public byte Command { get; set; }
        public ushort Block { get; set; }
        public byte[] Data { get; set; }

        public byte[] ToBytes()
        {
            List<byte> packet = new List<byte>
            {
                0xAA, // Start byte
                Command,
                (byte)(Command ^ 0xFF), // Command complement
                (byte)(Block & 0xFF),
                (byte)((Block >> 8) & 0xFF),
                (byte)(Data?.Length ?? 0),
                (byte)((Data?.Length ?? 0) >> 8)
            };
            if (Data != null) packet.AddRange(Data);
            byte checksum = CalculateChecksum(packet.ToArray());
            packet.Add(checksum);
            return packet.ToArray();
        }

        public static O2Packet Parse(byte[] receivedData)
        {
            if (receivedData.Length < 8 || receivedData[0] != 0x55)
                throw new Exception("Invalid packet start or length");

            byte command = receivedData[1];
            byte ncmd = receivedData[2];
            if (command != (ncmd ^ 0xFF)) throw new Exception("Command check failed");

            ushort block = BitConverter.ToUInt16(receivedData, 3);
            ushort dataLength = BitConverter.ToUInt16(receivedData, 5);
            if (receivedData.Length != 8 + dataLength) throw new Exception("Invalid data length");

            byte[] data = new byte[dataLength];
            Array.Copy(receivedData, 7, data, 0, dataLength);

            byte checksum = receivedData[receivedData.Length - 1];
            byte calculatedChecksum = CalculateChecksum(receivedData, 0, receivedData.Length - 1);
            if (checksum != calculatedChecksum) throw new Exception("Checksum failed");

            return new O2Packet { Command = command, Block = block, Data = data };
        }

        private static byte CalculateChecksum(byte[] data, int start = 0, int length = -1)
        {
            if (length == -1) length = data.Length - start;
            byte crc = 0;
            for (int i = start; i < start + length; i++)
            {
                byte chk = (byte)(crc ^ data[i]);
                crc = 0;
                if ((chk & 0x01) != 0) crc = 0x07;
                if ((chk & 0x02) != 0) crc ^= 0x0e;
                if ((chk & 0x04) != 0) crc ^= 0x1c;
                if ((chk & 0x08) != 0) crc ^= 0x38;
                if ((chk & 0x10) != 0) crc ^= 0x70;
                if ((chk & 0x20) != 0) crc ^= 0xe0;
                if ((chk & 0x40) != 0) crc ^= 0xc7;
                if ((chk & 0x80) != 0) crc ^= 0x89;
            }
            return crc;
        }
    }

    // Device management class for O2Ring
    public class O2RingDevice
    {
        private string macAddress;
        private string name;
        private bool isConnected;
        private List<byte> receiveBuffer = new List<byte>();
        private float nextReadTime;

        private O2RingBLEManager manager;

        public O2RingDevice(string mac, string name, O2RingBLEManager manager)
        {
            this.macAddress = mac;
            this.name = name;
            this.manager = manager;
        }

        public async Task ConnectAsync()
        {
            Debug.Log($"[{name}] Connecting to {macAddress}...");
            BluetoothLEHardwareInterface.ConnectToPeripheral(macAddress, (address) =>
            {
                Debug.Log($"[{name}] Connected to {address}");
            }, (address, serviceUUID) =>
            {
                Debug.Log($"[{name}] Service discovered: {serviceUUID}");
            }, (address, serviceUUID, characteristicUUID) =>
            {
                if (serviceUUID == O2Constants.BLE_SERVICE_UUID)
                {
                    if (characteristicUUID == O2Constants.BLE_READ_UUID || characteristicUUID == O2Constants.BLE_WRITE_UUID)
                    {
                        isConnected = true;
                        Debug.Log($"[{name}] Connected successfully");
                        BluetoothLEHardwareInterface.SubscribeCharacteristicWithDeviceAddress(macAddress, O2Constants.BLE_SERVICE_UUID, O2Constants.BLE_READ_UUID, (notifyAddress, notifyCharacteristic) =>
                        {
                            Debug.Log($"[{name}] Subscribed to notifications on {notifyCharacteristic}");
                        }, OnDataReceived);
                        SendCommandAsync(O2Constants.CMD_INFO); // CMD_INFO
                        nextReadTime = Time.time + 2f;
                    }
                }
            }, (disconnectedAddress) =>
            {
                Debug.LogWarning($"[{name}] Disconnected from {disconnectedAddress}");
                isConnected = false;
            });
        }

        public async Task SendCommandAsync(byte command, ushort block = 0, byte[] data = null)
        {
            O2Packet pkt = new O2Packet { Command = command, Block = block, Data = data };
            byte[] packetData = pkt.ToBytes();
            Debug.Log($"[{name}] Sending command {command}: {BitConverter.ToString(packetData)}");
            await WriteAsync(packetData);
        }

        private async Task WriteAsync(byte[] data)
        {
            const int chunkSize = 20;
            int offset = 0;
            while (offset < data.Length)
            {
                int size = Math.Min(chunkSize, data.Length - offset);
                byte[] chunk = new byte[size];
                Array.Copy(data, offset, chunk, 0, size);
                BluetoothLEHardwareInterface.WriteCharacteristic(macAddress, O2Constants.BLE_SERVICE_UUID, O2Constants.BLE_WRITE_UUID, chunk, chunk.Length, true, (characteristicUUID) =>
                {
                    Debug.Log($"[{name}] Write succeeded to {characteristicUUID}");
                });
                offset += size;
                await Task.Delay(100);
            }
        }

        public void OnDataReceived(string address, string characteristicUUID, byte[] data)
        {
            Debug.Log($"[{name}] Received raw BLE data chunk ({data.Length} bytes): {BitConverter.ToString(data)}");
            receiveBuffer.AddRange(data);
            ProcessReceiveBuffer();
        }

        private void ProcessReceiveBuffer()
        {
            Debug.Log($"[{name}] Processing receive buffer with {receiveBuffer.Count} bytes");
            while (receiveBuffer.Count >= 8)
            {
                int startIndex = receiveBuffer.IndexOf(0x55);
                if (startIndex == -1)
                {
                    Debug.LogWarning($"[{name}] No start byte (0x55) found, discarding {receiveBuffer.Count} bytes");
                    receiveBuffer.Clear();
                    return;
                }
                if (startIndex > 0)
                {
                    Debug.Log($"[{name}] Discarding {startIndex} bytes before start byte");
                    receiveBuffer.RemoveRange(0, startIndex);
                }

                if (receiveBuffer.Count < 8)
                {
                    Debug.Log($"[{name}] Not enough data for header, waiting for more (have {receiveBuffer.Count} bytes)");
                    return;
                }

                ushort dataLength = BitConverter.ToUInt16(receiveBuffer.ToArray(), 5);
                int totalPacketLength = 8 + dataLength;
                Debug.Log($"[{name}] Detected packet with data length {dataLength}, total length {totalPacketLength}");

                if (receiveBuffer.Count < totalPacketLength)
                {
                    Debug.Log($"[{name}] Incomplete packet, need {totalPacketLength} bytes, have {receiveBuffer.Count}, waiting");
                    return;
                }

                byte[] packetData = receiveBuffer.GetRange(0, totalPacketLength).ToArray();
                receiveBuffer.RemoveRange(0, totalPacketLength);
                Debug.Log($"[{name}] Extracted packet: {BitConverter.ToString(packetData)}");

                try
                {
                    O2Packet packet = O2Packet.Parse(packetData);
                    Debug.Log($"[{name}] Packet parsed successfully, command: {packet.Command}");
                    HandlePacket(packet);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{name}] Failed to parse packet: {ex.Message}");
                }
            }
            Debug.Log($"[{name}] Buffer processed, remaining bytes: {receiveBuffer.Count}");
        }

        private void HandlePacket(O2Packet packet)
        {
            Debug.Log($"[{name}] Handling packet with command {packet.Command}, data length {packet.Data.Length}");
            Debug.Log($"[{name}] Packet data: {BitConverter.ToString(packet.Data)}");
            ParseSensorData(packet.Data);
        }

        private void ParseSensorData(byte[] data)
        {
            Debug.Log($"[{name}] Parsing sensor data, length: {data.Length}");
            if (data.Length < 13)
            {
                Debug.LogError($"[{name}] Invalid sensor data length: {data.Length}, expected at least 13 bytes");
                return;
            }

            byte o2 = data[0];
            byte hr = data[1];
            byte batt = data[7];
            byte charging = data[8];
            byte motion = data[9];
            byte hr_strength = data[10];
            bool finger_present = data[11] != 0;
            
            //convert o2 and hr to int
            int o2Int = (int)o2;
            int hrInt = (int)hr;
            
            manager.o2 = o2Int;
            manager.bpm = hrInt;

            string battStatus = charging > 0 ? (charging == 1 ? $"{batt}%++" : "CHGD") : $"{batt}%";
            Debug.Log($"[{name}] Parsed sensor data - SpO2: {o2}%, HR: {hr} bpm, Perfusion Idx: {hr_strength}, Motion: {motion}, Battery: {battStatus}, Finger Present: {finger_present}");
        }

        private void ParseInfoData(byte[] data)
        {
            Debug.Log($"[{name}] Parsing info data, length: {data.Length}");
            string jsonString = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0', ' ', '\t', '\r', '\n');
            Debug.Log($"[{name}] Converted data to JSON string: {jsonString}");

            try
            {
                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
                string formattedConfig = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                Debug.Log($"[{name}] Parsed config: \n{formattedConfig}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{name}] Failed to parse config JSON: {ex.Message}");
            }
        }

        public void Update()
        {
            if (isConnected && Time.time >= nextReadTime)
            {
                SendCommandAsync(O2Constants.CMD_READ_SENSORS); // CMD_READ_SENSORS
                nextReadTime = Time.time + 2f;
            }
        }
    }

    // Manager class to handle scanning and device connections
    private Dictionary<string, O2RingDevice> devices = new Dictionary<string, O2RingDevice>();

    void Start()
    {
        Debug.Log("Initializing Bluetooth...");
        BluetoothLEHardwareInterface.Initialize(true, false, () =>
        {
            Debug.Log("Bluetooth initialized.");
            StartScanning();
        }, (error) =>
        {
            Debug.LogError($"Bluetooth initialization failed: {error}");
        });
    }

    public void StartScanning()
    {
        Debug.Log("Starting BLE scan...");
        BluetoothLEHardwareInterface.ScanForPeripheralsWithServices(null, (address, name) =>
        {
            Debug.Log($"Found device: {name} ({address})");
            if (IsO2Ring(name) && !devices.ContainsKey(address))
            {
                Debug.Log($"Found O2Ring device: {name} ({address})");
                O2RingDevice device = new O2RingDevice(address, name, this);
                devices[address] = device;
                device.ConnectAsync();
            }
        }, null);
    }

    private bool IsO2Ring(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string[] o2Names = { "O2", "CheckO2", "SleepU", "SleepO2", "O2Ring", "WearO2", "KidsO2", "BabyO2", "Oxylink" };
        foreach (string n in o2Names)
        {
            if (name.Contains(n)) return true;
        }
        return false;
    }

    void Update()
    {
        foreach (var device in devices.Values)
        {
            device.Update();
        }

        if (o2Text != null)
            o2Text.text = $"O2: {o2}%";

        if (bpmText != null)
            bpmText.text = $"BPM: {bpm}";
    }
}