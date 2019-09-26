using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Buttplug.Core;
using Buttplug.Core.Logging;
using Buttplug.Core.Messages;
using Buttplug.Devices;
using Buttplug.Server;
using Org.BouncyCastle.Crypto.Generators;

namespace IntifaceCLI
{
    internal class SimulatedDeviceAddedArgs
    {
        public ServerControlMessage.Types.AddSimulatedDevice addSimulatedDevice;

        public SimulatedDeviceAddedArgs(ServerControlMessage.Types.AddSimulatedDevice addSimulatedDevice)
        {
            this.addSimulatedDevice = addSimulatedDevice;
        }
    }

    internal class SimulatedDeviceRemovedArgs
    {
        public ServerControlMessage.Types.RemoveSimulatedDevice removeSimulatedDevice;

        public SimulatedDeviceRemovedArgs(ServerControlMessage.Types.RemoveSimulatedDevice removeSimulatedDevice)
        {
            this.removeSimulatedDevice = removeSimulatedDevice;
        }
    }

    internal class SimulatedDeviceMsgInArgs
    {
        public ServerControlMessage.Types.SimulatedDeviceMsgIn simulatedDeviceMsgIn;

        public SimulatedDeviceMsgInArgs(ServerControlMessage.Types.SimulatedDeviceMsgIn simulatedDeviceMsgIn)
        {
            this.simulatedDeviceMsgIn = simulatedDeviceMsgIn;
        }
    }

    internal class SimulatedDeviceMsgOutArgs
    {
        public ServerProcessMessage serverProcessMessage;

        public SimulatedDeviceMsgOutArgs(ServerProcessMessage serverProcessMessage)
        {
            this.serverProcessMessage = serverProcessMessage;
        }
    }

    internal class SimulatedDevice : IButtplugDevice
    {
        private bool connected = true;
        private string id;
        private string name;
        private Dictionary<Type, MessageAttributes> desc;

        internal SimulatedDevice(SimulatedDeviceManager aMgr, string aName, string aId, Dictionary<Type, MessageAttributes> aDeviceDescription)
        {
            manager = aMgr;
            name = aName;
            id = aId;
            desc = aDeviceDescription;
        }
        
        event EventHandler<MessageReceivedEventArgs> ForwardMessage;

        public void Disconnect()
        {
            connected = false;
            DeviceRemoved?.Invoke(this, EventArgs.Empty);
        }

        public async Task<ButtplugMessage> ParseMessageAsync(ButtplugDeviceMessage aMsg, CancellationToken aToken = new CancellationToken())
        {
            return await manager.ForwardMessage(Identifier, aMsg, aToken);
        }

        public Task InitializeAsync(CancellationToken aToken = new CancellationToken())
        {
            return Task.CompletedTask;
        }

        public MessageAttributes GetMessageAttrs(Type aMsg)
        {
            return desc.TryGetValue(aMsg, out var attrs) ? attrs : null;
        }
        public void PassThroughMsg(ButtplugDeviceMessage aMsg)
        {
            MessageEmitted?.Invoke(this, new MessageReceivedEventArgs(aMsg));
        }

        public string Name => name;
        public string Identifier => id;
        public bool Connected => connected;
        private SimulatedDeviceManager manager;
        public IEnumerable<Type> AllowedMessageTypes => desc.Keys;

        bool IButtplugDevice.Connected => connected;

        public event EventHandler DeviceRemoved;
        public event EventHandler<MessageReceivedEventArgs> MessageEmitted;
    }

    internal class SimulatedDeviceManager : TimedScanDeviceSubtypeManager
    {
        internal event EventHandler<SimulatedDeviceMsgOutArgs> SimulatedDeviceMsgOut;
        private readonly object devLock = new object();
        private readonly ConcurrentDictionary<string, SimulatedDevice> _devices = new ConcurrentDictionary<string, SimulatedDevice>();
        private readonly ConcurrentDictionary<string, SimulatedDevice> _pendingDevices = new ConcurrentDictionary<string, SimulatedDevice>();
        private ButtplugJsonMessageParser _parser;


        public SimulatedDeviceManager(IButtplugLogManager aLogManager) : base(aLogManager)
        {
            _parser = new ButtplugJsonMessageParser(aLogManager);
            aLogManager.GetLogger(GetType()).Info("Simulator loaded!");
        }

        protected override void RunScan()
        {
            lock (devLock)
            {
                foreach (var dev in _pendingDevices.Values.ToList())
                {
                    if (!_devices.TryAdd(dev.Identifier, dev))
                    {
                        continue;
                    }

                    _pendingDevices.TryRemove(dev.Identifier, out _);
                    InvokeDeviceAdded(new DeviceAddedEventArgs(dev));
                }
            }
        }

        public void AddDevice(SimulatedDevice dev)
        {
            lock (devLock)
            {
                if (_devices.TryGetValue(dev.Identifier, out var old))
                {
                    old.Disconnect();
                }

                _pendingDevices.AddOrUpdate(dev.Identifier, dev, (aS, aDevice) => dev);
            }
        }

        public void RemoveDevice(string devIdent)
        {
            lock (devLock)
            {
                if (_devices.TryGetValue(devIdent, out var dev2))
                {
                    dev2.Disconnect();
                }
                else
                {
                    _pendingDevices.TryRemove(devIdent, out _);
                }
            }
        }

        internal async Task<ButtplugMessage> ForwardMessage(string identifier, ButtplugDeviceMessage aMsg, CancellationToken aToken)
        {
            var output = new ServerProcessMessage
            {
                SimulatedDeviceMsgOut = new ServerProcessMessage.Types.SimulatedDeviceMsgOut
                {
                    DeviceIdent = identifier,
                    JsonMsg = _parser.Serialize(aMsg, ButtplugConsts.CurrentSpecVersion)
                }
            };

            SimulatedDeviceMsgOut?.Invoke(this, new SimulatedDeviceMsgOutArgs(output));
            return await Task.FromResult(new Ok(aMsg.Id));
        }

        public void RepeatMessage(string aDeviceIdent, IEnumerable<ButtplugMessage> aDeserialize)
        {
            lock (devLock)
            {
                if (!_devices.TryGetValue(aDeviceIdent, out var dev))
                {
                    return;
                }

                foreach (var msg in aDeserialize)
                {
                    if (msg is ButtplugDeviceMessage dMsg)
                    {
                        dev.PassThroughMsg(dMsg);
                    }
                }
            }
        }

        internal void HandleDeviceAdded(object aSender, SimulatedDeviceAddedArgs aArgs)
        {
            var devMsgTypes = from a in AppDomain.CurrentDomain.GetAssemblies()
                from t in a.GetTypes()
                where t.IsSubclassOf(typeof(ButtplugDeviceMessage)) && !t.IsAbstract
                select t;
            var msgs = new Dictionary<Type, MessageAttributes>();
            foreach (var deviceMessageAttrs in aArgs.addSimulatedDevice.DeviceMsgs)
            {
                var type = devMsgTypes.ToList().First(t => t.Name == deviceMessageAttrs.Key);
                if (type == null)
                {
                    continue;
                }

                var attrs = new MessageAttributes();
                if (deviceMessageAttrs.Value.MsgsAttrs.TryGetValue("FeatureCount",
                    out var value))
                {
                    attrs.FeatureCount = Convert.ToUInt32(value);
                }

                msgs.Add(type, attrs);
            }

            AddDevice(new SimulatedDevice(this,
                aArgs.addSimulatedDevice.DeviceName, aArgs.addSimulatedDevice.DeviceIdent,
                msgs));
        }

        public void HandleDeviceRemoved(object aSender, SimulatedDeviceRemovedArgs aArgs)
        {
            RemoveDevice(aArgs.removeSimulatedDevice.DeviceIdent);
        }

        public void SimulatedDeviceMsgIn(object aSender, SimulatedDeviceMsgInArgs aArgs)
        {
            RepeatMessage(aArgs.simulatedDeviceMsgIn.DeviceIdent, _parser.Deserialize(aArgs.simulatedDeviceMsgIn.JsonMsg));
        }
    }
}
