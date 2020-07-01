namespace nsgFunc
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Net;

    public class TemplateFlow
    {
        private readonly List<NetFlowElement> _elements;

        public TemplateFlow(ushort id)
        {
            Id = id;
            _elements = new List<NetFlowElement>();
        }

        public TemplateFlow(ushort id, params NetFlowElement[] elements)
        {
            Id = id;
            _elements = elements.ToList();
        }

        public TemplateFlow Field(NetFlowInformationElement fieldId, ushort size)
        {
            var field = new NetFlowElement(fieldId, size);
            _elements.Add(field);
            return this;
        }

        public ushort Id { get; }

        public ushort FieldCount => (ushort)_elements.Count;

        public NetFlowElement this[int index] => _elements[index];

        public void Generate(PacketEncoder packet)
        {
            var length = (ushort)(2 + 2 + 2 + 2 + FieldCount * (2 + 2));

            // Template FlowSet Id
            packet.AddInt16(0);

            // Length
            packet.AddInt16(length);

            // Template ID
            packet.AddInt16(Id);

            // FieldCount
            packet.AddInt16(FieldCount);

            foreach (var field in _elements)
            {
                field.EncodeTo(packet);
            }
        }
    }

    public class DataFlow
    {
        private readonly TemplateFlow _template;
        private readonly List<byte[]> _dataValues = new List<byte[]>();
        private readonly ushort _dataLength;
        private readonly ushort _paddingLength;

        public DataFlow(TemplateFlow template, params object[] values)
        {
            _template = template;

            if (values.Length != template.FieldCount)
                throw new ArgumentException(
                    $"Count of {nameof(_template.FieldCount)} must match count of {nameof(values)}. Expected {nameof(_template.FieldCount)}:{_template.FieldCount}. Provided {nameof(values)}:{values.Length}.");

            for (var i = 0; i < values.Length; i++)
            {
                var data = ToBytes(_template[i], values[i]);
                _dataValues.Add(data);
                _dataLength = (ushort)(_dataLength + data.Length);
            }

            if (_dataLength % 4 == 0) return;

            _paddingLength = (ushort)(4 - _dataLength % 4);
            _dataLength = (ushort)(_dataLength + _paddingLength);
        }

        public void Generate(PacketEncoder packet)
        {
            // FlowSet ID
            packet.AddInt16(_template.Id);

            // Length
            packet.AddInt16((ushort)(2 + 2 + _dataLength));

            foreach (var data in _dataValues)
            {
                packet.Add(data);
            }

            if (_paddingLength == 0) return;
            packet.Add(new byte[_paddingLength]);
        }

        public byte[] ToBytes(NetFlowElement element, object value)
        {

            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.UInt32:
                    {
                        var data = BitConverter.GetBytes((UInt32)value);
                        Array.Reverse(data);
                        return data;
                    }
                case TypeCode.UInt16:
                    {
                        var data = BitConverter.GetBytes((UInt16)value);
                        Array.Reverse(data);
                        return data;
                    }
                case TypeCode.Byte:
                    {
                        var data = new byte[1];
                        data[0] = (byte)value;
                        return data;
                    }

                case TypeCode.String:
                    {
                        var length = (ushort)((string)value).Length;
                        if (length > element.Size)
                        {
                            throw new ArgumentException($"Invalid length of string:{length} expected:{element.Size}");
                        }

                        byte[] data = new byte[element.Size == 65535 ? length + 1 : element.Size];
                        var charData = System.Text.Encoding.ASCII.GetBytes((string)value);

                        if(element.Size == 65535)
                        {
                            /*
                             65535 is used to designate a variable length element, which stores its length as the first
                             byte of the element value.  This is required for fields such as 82, InterfaceName
                             See: https://tools.ietf.org/html/rfc7011#section-7 for more details
                            */

                            // copy the length of the string as the first element
                            var lengthBytes = BitConverter.GetBytes(length);
                            Buffer.BlockCopy(lengthBytes, 0, data, 0, lengthBytes.Length * sizeof(ushort));
                            Buffer.BlockCopy(charData, 0, data, lengthBytes.Length * sizeof(int), Math.Min(charData.Length, data.Length) * sizeof(ushort));
                        }
                        else
                        {
                            // treat elements that do not have a size of 65535 as non-variable
                            Buffer.BlockCopy(charData, 0, data, 0, Math.Min(charData.Length, data.Length) * sizeof(ushort));
                        }

                        return data;
                    }

                case TypeCode.Object:
                    {
                        if (value is IPAddress)
                        {
                            var data = ((IPAddress)value).GetAddressBytes();
                            return data;
                        }
                    }
                    break;
            }

            throw new ArgumentException("Invalid value provided.");
        }
    }

    public class TemplateData
    {
        private readonly TemplateFlow _template;
        private readonly List<DataFlow> _data = new List<DataFlow>();

        public TemplateData(TemplateFlow template)
        {
            _template = template;
        }

        public ushort DataCount => (ushort)_data.Count;

        public void AddData(params object[] values)
        {
            _data.Add(new DataFlow(_template, values));
        }

        public TemplateData Data(params object[] values)
        {
            AddData(values);
            return this;
        }

        public void Generate(PacketEncoder packet)
        {
            _template.Generate(packet);
            foreach (var data in _data)
            {
                data.Generate(packet);
            }
        }
    }

    public class ExportPacket
    {
        private readonly ushort _sequence;
        private readonly ushort _sourceId;
        private readonly List<TemplateData> _dataFlows = new List<TemplateData>();

        public ExportPacket(ushort sequence, ushort sourceId)
        {
            _sequence = sequence;
            _sourceId = sourceId;
        }

        public ExportPacket Template(TemplateData dataFlow)
        {
            Add(dataFlow);
            return this;
        }

        public void Add(TemplateData dataFlow)
        {
            _dataFlows.Add(dataFlow);
        }

        public void Generate(PacketEncoder packet)
        {
            var count = (ushort)_dataFlows.Sum(x => 1 + x.DataCount);
            packet.AddInt16(9);  // Version
            packet.AddInt16(count); //Number of Flow sets
            packet.AddInt32(GetUpTimeMs()); //sysUpTime
            packet.AddInt32(GetEpoch()); // UNIX Secs
            packet.AddInt32(_sequence); // sequence number
            packet.AddInt32(_sourceId); // source id

            foreach (var dataFlow in _dataFlows)
            {
                dataFlow.Generate(packet);
            }
        }

        public byte[] GetData()
        {
            var packet = new PacketEncoder();
            Generate(packet);
            return packet.Data;
        }

        public uint GetEpoch()
        {
            return (uint)(DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        public uint GetUpTimeMs()
        {
            return (uint)Environment.TickCount;
        }
    }

    public class NetFlowElement
    {
        private readonly NetFlowInformationElement _elementId;

        public NetFlowElement(NetFlowInformationElement elementId, ushort size)
        {
            _elementId = elementId;
            Size = size;
        }

        public ushort Size { get; }

        public void EncodeTo(PacketEncoder packet)
        {
            packet.AddInt16((ushort)_elementId);
            packet.AddInt16(Size);

        }
    }

    /// <summary>
    /// <see href="https://www.iana.org/assignments/ipfix/ipfix.xhtml"/>
    /// <seealso href="https://tools.ietf.org/html/rfc5102"/>
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("ReSharper", "IdentifierTypo")]
    public enum NetFlowInformationElement : ushort
    {
        UNKNOWN = 0,
        InputBytes = 1,
        InputPackets = 2,
        Flows = 3,
        Protocol = 4,
        SourceTypeOfService = 5,
        TcpFlags = 6,
        L4SourcePort = 7,
        IPV4SourceAddress = 8,
        SourceMask = 9,
        InputSNMP = 10,
        L4DestionationPort = 11,
        IPV4DestionationAddress = 12,
        DestionationMask = 13,
        OoutputSNMP = 14,
        IPV4NextHop = 15,
        SourceAS = 16,
        DestionationAS = 17,
        BgpIPV4NextHop = 18,
        MulticastDestionationPackets = 19,
        MulticastDestionationBytes = 20,
        LastSwitched = 21,
        FirstSwitched = 22,
        OutputBytes = 23,
        OutputPackets = 24,
        MinPacketLength = 25,
        MaxPacketLength = 26,
        IPV6SourceAddress = 27,
        IPV6DestionationAddress = 28,
        IPV6SourceMask = 29,
        IPV6DestionationMask = 30,
        IPV6FlowLabel = 31,
        IcmpType = 32,
        MulticastIgmpType = 33,
        SamplingInterval = 34,
        SamplingAlgorithm = 35,
        FlowActiveTimeout = 36,
        FlowInactiveTimeout = 37,
        EngineType = 38,
        EngineId = 39,
        TotalBytesExported = 40,
        TotalPacketsExported = 41,
        TotalFlowsExported = 42,
        IPV4SourcePrefix = 44,
        IPV4DestionationPrefix = 45,
        MplsTopLabelType = 46,
        MplsTopLabelIpAddress = 47,
        FlowSamplerId = 48,
        FlowSamplerMode = 49,
        FlowSamplerRandomInterval = 50,
        MinTtl = 52,
        MaxTtl = 53,
        IPV4Ident = 54,
        DestionatioTypeOfService = 55,
        InputSourceMAC = 56,
        OutputDestionation_MAC = 57,
        SourceVLAN = 58,
        DestionationVLAN = 59,
        IpProtocolVersion = 60,
        Direction = 61,
        IPV6NextHop = 62,
        BpgIPV6NextHop = 63,
        IPV6OptionHeaders = 64,
        MplsLabel1 = 70,
        MplsLabel2 = 71,
        MplsLabel3 = 72,
        MplsLabel4 = 73,
        MplsLabel5 = 74,
        MplsLabel6 = 75,
        MplsLabel7 = 76,
        MplsLabel8 = 77,
        MplsLabel9 = 78,
        MplsLabel10 = 79,
        InputDestionationMac = 80,
        OutputSourceMac = 81,
        InterfaceName = 82,
        InterfaceDescription = 83,
        SamplerName = 84,
        InputPermanentBytes = 85,
        InputPermanentPackets = 86,
        FragmentOffset = 88,
        ForwardingStatus = 89,
        MplsPalRouteDistinguisher = 90,
        MplsPrefixLength = 91,
        SourceTrafficIndex = 92,
        DestionationTrafficIndex = 93,
        ApplicationDescription = 94,
        ApplicationTag = 95,
        ApplicationName = 96,
        PostIpDiffServCodePoint = 98,
        ReplicationFactor = 99,
        L2PacketSectionOffset = 102,
        L2PacketSectionSize = 103,
        L2packetSectionData = 104
    }

    public class PacketEncoder
    {
        private byte[] _data = new byte[0];

        public byte[] Data => _data;

        public void AddInt32(uint val)
        {
            Add(BitConverter.GetBytes(val).Reverse().ToArray());
        }

        public void AddInt16(ushort val)
        {
            Add(BitConverter.GetBytes(val).Reverse().ToArray());
        }

        public void AddIPv4(string ipStr)
        {
            Add(IPAddress.Parse(ipStr).GetAddressBytes());
        }

        public void Add(byte[] data)
        {
            var index = _data.Length;
            Array.Resize(ref _data, _data.Length + data.Length);
            Array.Copy(data, 0, _data, index, data.Length);
        }
    }
}
