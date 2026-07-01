using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hsms.Public
{
    public sealed class OvenClient
    {
        private const int StateRun = 2;

        private readonly string _ipAddress;
        private readonly int _port;
        private readonly ushort _deviceId;

        public string LastError { get; private set; }

        public string LastSentSml { get; private set; }

        public string LastReceivedSml { get; private set; }

        public OvenClient(string ipAddress, int port)
            : this(ipAddress, port, false)
        {
        }

        public OvenClient(string ipAddress, int port, bool passive)
            : this(0, ipAddress, port, !passive)
        {
        }

        public OvenClient(ushort deviceId, string ipAddress, int port, bool active)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                throw new ArgumentException("IP address is required.", "ipAddress");
            }

            _deviceId = deviceId;
            _ipAddress = ipAddress;
            _port = port;
            LastError = string.Empty;
            LastSentSml = string.Empty;
            LastReceivedSml = string.Empty;
        }

        public bool CheckConnection()
        {
            return Execute(client =>
            {
                return EstablishCommunicate(client);
            }, false);
        }

        public string[] GetPpid()
        {
            return Execute(client =>
            {
                if (!EstablishCommunicate(client))
                {
                    LastError = "Equipment not responding.";
                    return null;
                }

                var reply = client.SendSecs(7, 19, true, null);
                LastReceivedSml = reply == null ? string.Empty : reply.ToSml();
                return reply == null ? null : reply.Item.TextValues().Where(x => x.Length > 0).ToArray();
            }, null);
        }

        public Dictionary<string, object> ReadParameters()
        {
            return ReadParameters(_ipAddress);
        }

        public Dictionary<string, object> ReadParameters(string ip)
        {
            return Execute(client =>
            {
                var data = ReadParametersCore(client, ip);
                return data == null
                    ? null
                    : new Dictionary<string, object>
                    {
                        { "ppid", data.Ppid },
                        { "ip", data.Ip },
                        { "CurrentSegment", data.CurrentSegment },
                        { "total_steps", data.TotalSteps },
                        { "remain_time_hour", data.RemainTimeHour },
                        { "remain_time_min", data.RemainTimeMin },
                        { "CurrentTemp", data.CurrentTemp },
                        { "OperatingStatus", data.OperatingStatus },
                        { "CurrentTempSetting", data.CurrentTempSetting }
                    };
            }, null);
        }

        public OvenParameters ReadParametersData()
        {
            return ReadParametersData(_ipAddress);
        }

        public OvenParameters ReadParametersData(string ip)
        {
            return Execute(client => ReadParametersCore(client, ip), null);
        }

        public string ReadParametersText()
        {
            var data = ReadParametersData(_ipAddress);
            if (data == null)
            {
                return string.Empty;
            }

            return "PPID:" + data.Ppid
                + ";IP:" + data.Ip
                + ";CurrentStep:" + data.CurrentSegment.ToString(CultureInfo.InvariantCulture)
                + ";TotalSteps:" + data.TotalSteps.ToString(CultureInfo.InvariantCulture)
                + ";remain_time_hour:" + data.RemainTimeHour.ToString(CultureInfo.InvariantCulture)
                + ";remain_time_min:" + data.RemainTimeMin.ToString(CultureInfo.InvariantCulture)
                + ";CurrentTemp:" + data.CurrentTemp.ToString(CultureInfo.InvariantCulture)
                + ";OperatingStatus:" + data.OperatingStatus
                + ";CurrentTempSetting:" + data.CurrentTempSetting.ToString(CultureInfo.InvariantCulture);
        }

        public string[] ReadParametersStringArray()
        {
            var data = ReadParametersData(_ipAddress);
            if (data == null)
            {
                return Array.Empty<string>();
            }

            return new[]
            {
                data.Ppid,
                data.Ip,
                data.CurrentSegment.ToString(CultureInfo.InvariantCulture),
                data.TotalSteps.ToString(CultureInfo.InvariantCulture),
                data.RemainTimeHour.ToString(CultureInfo.InvariantCulture),
                data.RemainTimeMin.ToString(CultureInfo.InvariantCulture),
                data.CurrentTemp.ToString(CultureInfo.InvariantCulture),
                data.OperatingStatus,
                data.CurrentTempSetting.ToString(CultureInfo.InvariantCulture)
            };
        }

        public string[,] ReadParametersStringTable()
        {
            var data = ReadParametersData(_ipAddress);
            if (data == null)
            {
                return new string[0, 0];
            }

            return new[,]
            {
                { "PPID", data.Ppid },
                { "IP", data.Ip },
                { "CurrentStep", data.CurrentSegment.ToString(CultureInfo.InvariantCulture) },
                { "TotalSteps", data.TotalSteps.ToString(CultureInfo.InvariantCulture) },
                { "remain_time_hour", data.RemainTimeHour.ToString(CultureInfo.InvariantCulture) },
                { "remain_time_min", data.RemainTimeMin.ToString(CultureInfo.InvariantCulture) },
                { "CurrentTemp", data.CurrentTemp.ToString(CultureInfo.InvariantCulture) },
                { "OperatingStatus", data.OperatingStatus },
                { "CurrentTempSetting", data.CurrentTempSetting.ToString(CultureInfo.InvariantCulture) }
            };
        }

        private OvenParameters ReadParametersCore(RawHsmsClient client, string ip)
        {
            if (!EstablishCommunicate(client))
            {
                LastError = "Equipment not responding.";
                return null;
            }

            var status = ReadStatus(client);
            if (status == null || status.Length < 7)
            {
                throw new InvalidOperationException("S1F4 status response does not contain all requested values.");
            }

            var ppid = Convert.ToString(status[0], CultureInfo.InvariantCulture);
            var ppbody = RequestProcessProgram(client, ppid);
            var remainTime = ToInt32(status[2]) - ToInt32(status[3]);
            var statusCode = Convert.ToString(status[5], CultureInfo.InvariantCulture);

            return new OvenParameters
            {
                Ppid = ppid,
                Ip = ip,
                CurrentSegment = ToInt32(status[1]),
                TotalSteps = CountSteps(ppbody),
                RemainTimeHour = remainTime / 60,
                RemainTimeMin = remainTime % 60,
                CurrentTemp = ToInt32(status[4]),
                OperatingStatus = MapOperatingStatus(statusCode),
                CurrentTempSetting = ToInt32(status[6])
            };
        }

        public bool StartProgram(string ppid)
        {
            if (string.IsNullOrWhiteSpace(ppid))
            {
                LastError = "PPID is required.";
                return false;
            }

            return Execute(client =>
            {
                if (!EstablishCommunicate(client))
                {
                    LastError = "Equipment not responding.";
                    return false;
                }

                var state = ToInt32(ReadOneSvid(client, 15));
                var ppidList = GetProcessProgramList(client);
                if (ppidList == null || !ppidList.Contains(ppid))
                {
                    LastError = "PPID not found on this device.";
                    return false;
                }

                if (state == StateRun)
                {
                    LastError = "Machine is RUNNING, cannot start new program.";
                    return false;
                }

                if (!EnsureRemote(client))
                {
                    LastError = "Request REMOTE failed.";
                    return false;
                }

                if (!string.Equals(Convert.ToString(ReadOneSvid(client, 2302), CultureInfo.InvariantCulture), ppid, StringComparison.Ordinal))
                {
                    if (!RemoteCommand(client, "PPSELECT", SecsItem.L(SecsItem.L(SecsItem.A("PPID"), SecsItem.A(ppid)))))
                    {
                        LastError = "PPSELECT failed.";
                        return false;
                    }

                    Thread.Sleep(2000);
                }

                if (ToInt32(ReadOneSvid(client, 2202)) != 1)
                {
                    LastError = "Door state open.";
                    return false;
                }

                if (ToInt32(ReadOneSvid(client, 2303)) != 1)
                {
                    LastError = "Step not 1.";
                    return false;
                }

                if (!RemoteCommand(client, "START", null))
                {
                    LastError = "START failed.";
                    return false;
                }

                return ToInt32(ReadOneSvid(client, 15)) == StateRun;
            }, false);
        }

        private T Execute<T>(Func<RawHsmsClient, T> action, T failureValue)
        {
            LastError = string.Empty;
            LastSentSml = string.Empty;
            LastReceivedSml = string.Empty;

            try
            {
                using (var client = new RawHsmsClient(_ipAddress, _port, _deviceId))
                {
                    client.TraceChanged += (sent, received) =>
                    {
                        LastSentSml = sent ?? LastSentSml;
                        LastReceivedSml = received ?? LastReceivedSml;
                    };

                    client.Connect();
                    Thread.Sleep(500);
                    return action(client);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return failureValue;
            }
            finally
            {
                Thread.Sleep(500);
            }
        }

        private static bool EnsureRemote(RawHsmsClient client)
        {
            var controlState = ToInt32(ReadOneSvid(client, 4));
            if (controlState == 5)
            {
                return true;
            }

            if (controlState != 4)
            {
                var online = client.SendSecs(1, 17, true, null);
                if (ToInt32(online == null ? null : online.Item.FirstValue()) != 0)
                {
                    return false;
                }
            }

            if (!RemoteCommand(client, "REMOTE", null))
            {
                return false;
            }

            Thread.Sleep(500);
            return ToInt32(ReadOneSvid(client, 4)) == 5;
        }

        private static bool EstablishCommunicate(RawHsmsClient client)
        {
            var reply = client.TrySendSecs(1, 13, true, SecsItem.L());
            if (reply != null)
            {
                return true;
            }

            reply = client.TrySendSecs(1, 1, true, null);
            return reply != null;
        }

        private static bool RemoteCommand(RawHsmsClient client, string command, SecsItem parameters)
        {
            var item = parameters == null
                ? SecsItem.L(SecsItem.A(command))
                : SecsItem.L(SecsItem.A(command), parameters);
            var reply = client.SendSecs(2, 41, true, item);
            return ToInt32(reply == null ? null : reply.Item.FirstValue()) == 0;
        }

        private static object ReadOneSvid(RawHsmsClient client, uint svid)
        {
            var reply = client.SendSecs(1, 3, true, SecsItem.L(SecsItem.U4(svid)));
            return reply == null ? null : reply.Item.FirstValue();
        }

        private static object[] ReadStatus(RawHsmsClient client)
        {
            var reply = client.SendSecs(
                1,
                3,
                true,
                SecsItem.L(
                    SecsItem.U4(2302),
                    SecsItem.U4(2303),
                    SecsItem.U4(2304),
                    SecsItem.U4(2305),
                    SecsItem.U4(2311),
                    SecsItem.U4(15),
                    SecsItem.U4(2205)));
            return reply == null ? null : reply.Item.Values().ToArray();
        }

        private static string[] GetProcessProgramList(RawHsmsClient client)
        {
            var reply = client.SendSecs(7, 19, true, null);
            return reply == null ? null : reply.Item.TextValues().Where(x => x.Length > 0).ToArray();
        }

        private static string RequestProcessProgram(RawHsmsClient client, string ppid)
        {
            var reply = client.SendSecs(7, 5, true, SecsItem.L(SecsItem.A(ppid)));
            if (reply != null && reply.Item != null && reply.Item.Children.Count == 0)
            {
                reply = client.TrySendSecs(7, 5, true, SecsItem.A(ppid));
            }

            if (reply == null || reply.Item == null)
            {
                return string.Empty;
            }

            return reply.Item.Children.Count >= 2 ? reply.Item.Children[1].AsText() : reply.Item.AsText();
        }

        private static int ToInt32(object value)
        {
            if (value == null || string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture)))
            {
                return 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static int CountSteps(string ppbody)
        {
            return string.IsNullOrEmpty(ppbody)
                ? 0
                : Regex.Matches(ppbody, @"\[STEP\d+\]").Cast<Match>().Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        private static string MapOperatingStatus(string statusCode)
        {
            return statusCode switch
            {
                "1" => "Idle",
                "2" => "Run",
                "3" => "End",
                _ => "Unknown_state",
            };
        }

        private sealed class RawHsmsClient : IDisposable
        {
            private readonly string _ipAddress;
            private readonly int _port;
            private readonly ushort _deviceId;
            private readonly byte[] _buffer = new byte[4];
            private TcpClient _tcpClient;
            private NetworkStream _stream;
            private uint _systemBytes = 1;

            public event Action<string, string> TraceChanged;

            public RawHsmsClient(string ipAddress, int port, ushort deviceId)
            {
                _ipAddress = ipAddress;
                _port = port;
                _deviceId = deviceId;
            }

            public void Connect()
            {
                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = 5000;
                _tcpClient.SendTimeout = 5000;
                _tcpClient.Connect(_ipAddress, _port);
                _stream = _tcpClient.GetStream();
                SendControl(1);

                while (true)
                {
                    var message = ReceiveMessage();
                    if (message.SType == 2)
                    {
                        return;
                    }

                    ReplyIfNeeded(message);
                }
            }

            public SecsMessage SendSecs(byte stream, byte function, bool wait, SecsItem item)
            {
                var systemBytes = NextSystemBytes();
                var header = CreateHeader(stream, function, wait, 0, systemBytes);
                var body = item == null ? new byte[0] : item.Encode();
                TraceChanged?.Invoke(ToSml(stream, function, wait, item), null);
                SendPacket(header, body);

                while (wait)
                {
                    var message = ReceiveMessage();
                    if (message.SType == 0 && message.SystemBytes == systemBytes)
                    {
                        TraceChanged?.Invoke(null, message.ToSml());
                        return message;
                    }

                    ReplyIfNeeded(message);
                }

                return null;
            }

            public SecsMessage TrySendSecs(byte stream, byte function, bool wait, SecsItem item)
            {
                try
                {
                    return SendSecs(stream, function, wait, item);
                }
                catch (Exception ex)
                {
                    TraceChanged?.Invoke(null, ex.Message);
                    return null;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (_stream != null && _tcpClient != null && _tcpClient.Connected)
                    {
                        SendControl(9);
                    }
                }
                catch
                {
                }

                _stream?.Dispose();
                _tcpClient?.Close();
            }

            private void SendControl(byte sType)
            {
                SendPacket(CreateHeader(0, 0, false, sType, NextSystemBytes()), new byte[0]);
            }

            private void ReplyIfNeeded(SecsMessage message)
            {
                if (message.SType != 0)
                {
                    if (message.SType == 5)
                    {
                        SendPacket(CreateHeader(0, 0, false, 6, message.SystemBytes), new byte[0]);
                    }

                    return;
                }

                if (message.Stream == 1 && message.Function == 1)
                {
                    SendPacket(CreateHeader(1, 2, false, 0, message.SystemBytes), SecsItem.L().Encode());
                    return;
                }

                if (message.Stream == 1 && message.Function == 13)
                {
                    SendPacket(
                        CreateHeader(1, 14, false, 0, message.SystemBytes),
                        SecsItem.L(SecsItem.B(0), SecsItem.L(SecsItem.A(string.Empty), SecsItem.A(string.Empty))).Encode());
                }
            }

            private SecsMessage ReceiveMessage()
            {
                ReadExact(_buffer, 0, 4);
                var length = ToUInt32(_buffer, 0);
                var payload = new byte[length];
                ReadExact(payload, 0, payload.Length);
                return SecsMessage.Parse(payload);
            }

            private void SendPacket(byte[] header, byte[] body)
            {
                var length = header.Length + body.Length;
                var packet = new byte[4 + length];
                WriteUInt32(packet, 0, (uint)length);
                Buffer.BlockCopy(header, 0, packet, 4, header.Length);
                Buffer.BlockCopy(body, 0, packet, 14, body.Length);
                _stream.Write(packet, 0, packet.Length);
            }

            private byte[] CreateHeader(byte stream, byte function, bool wait, byte sType, uint systemBytes)
            {
                var header = new byte[10];
                header[0] = (byte)(_deviceId >> 8);
                header[1] = (byte)_deviceId;
                header[2] = (byte)(stream | (wait ? 0x80 : 0));
                header[3] = function;
                header[4] = 0;
                header[5] = sType;
                WriteUInt32(header, 6, systemBytes);
                return header;
            }

            private uint NextSystemBytes()
            {
                return _systemBytes++;
            }

            private void ReadExact(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    var read = _stream.Read(buffer, offset, count);
                    if (read <= 0)
                    {
                        throw new IOException("HSMS connection closed.");
                    }

                    offset += read;
                    count -= read;
                }
            }
        }

        private sealed class SecsMessage
        {
            public byte Stream;
            public byte Function;
            public byte SType;
            public uint SystemBytes;
            public SecsItem Item;

            public string ToSml()
            {
                return OvenClient.ToSml(Stream, Function, false, Item);
            }

            public static SecsMessage Parse(byte[] payload)
            {
                var message = new SecsMessage
                {
                    Stream = (byte)(payload[2] & 0x7F),
                    Function = payload[3],
                    SType = payload[5],
                    SystemBytes = ToUInt32(payload, 6)
                };

                if (payload.Length > 10)
                {
                    var offset = 10;
                    message.Item = SecsItem.Decode(payload, ref offset);
                }

                return message;
            }
        }

        private sealed class SecsItem
        {
            public byte Format;
            public byte[] Data = new byte[0];
            public List<SecsItem> Children = new List<SecsItem>();

            public static SecsItem L(params SecsItem[] children)
            {
                return new SecsItem { Format = 0x00, Children = children == null ? new List<SecsItem>() : children.ToList() };
            }

            public static SecsItem A(string value)
            {
                return new SecsItem { Format = 0x40, Data = Encoding.ASCII.GetBytes(value ?? string.Empty) };
            }

            public static SecsItem B(params byte[] values)
            {
                return new SecsItem { Format = 0x20, Data = values ?? new byte[0] };
            }

            public static SecsItem U4(params uint[] values)
            {
                var data = new byte[(values ?? new uint[0]).Length * 4];
                for (var i = 0; i < values.Length; i++)
                {
                    WriteUInt32(data, i * 4, values[i]);
                }

                return new SecsItem { Format = 0xB0, Data = data };
            }

            public byte[] Encode()
            {
                var body = Format == 0x00 ? Children.SelectMany(x => x.Encode()).ToArray() : Data;
                var length = Format == 0x00 ? Children.Count : body.Length;
                using (var stream = new MemoryStream())
                {
                    WriteItemHeader(stream, Format, length);
                    stream.Write(body, 0, body.Length);
                    return stream.ToArray();
                }
            }

            public object FirstValue()
            {
                return Values().FirstOrDefault();
            }

            public IEnumerable<object> Values()
            {
                if (Format == 0x00)
                {
                    return Children.SelectMany(x => x.Values());
                }

                return ScalarValues();
            }

            public IEnumerable<string> TextValues()
            {
                return Values().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture));
            }

            public string AsText()
            {
                if (Format == 0x40)
                {
                    return Encoding.ASCII.GetString(Data).TrimEnd('\0').Trim();
                }

                if (Format == 0x20 && IsTextBytes(Data))
                {
                    return Encoding.ASCII.GetString(Data).TrimEnd('\0').Trim();
                }

                return Convert.ToString(FirstValue(), CultureInfo.InvariantCulture);
            }

            public string ToSml()
            {
                if (Format == 0x00)
                {
                    return "<L[" + Children.Count.ToString(CultureInfo.InvariantCulture) + "]" + string.Concat(Children.Select(x => x.ToSml())) + ">";
                }

                if (Format == 0x40)
                {
                    return "<A[" + Data.Length.ToString(CultureInfo.InvariantCulture) + "]\"" + AsText() + "\">";
                }

                if (Format == 0x20)
                {
                    return "<B[" + Data.Length.ToString(CultureInfo.InvariantCulture) + "]" + string.Join(" ", Data.Select(x => "0x" + x.ToString("X2", CultureInfo.InvariantCulture))) + ">";
                }

                return "<" + FormatName(Format) + "[" + Values().Count().ToString(CultureInfo.InvariantCulture) + "]" + string.Join(" ", Values()) + ">";
            }

            public static SecsItem Decode(byte[] payload, ref int offset)
            {
                var format = (byte)(payload[offset] & 0xFC);
                var lengthBytes = payload[offset] & 0x03;
                offset++;
                var length = 0;
                for (var i = 0; i < lengthBytes; i++)
                {
                    length = (length << 8) | payload[offset++];
                }

                var item = new SecsItem { Format = format };
                if (format == 0x00)
                {
                    for (var i = 0; i < length; i++)
                    {
                        item.Children.Add(Decode(payload, ref offset));
                    }
                }
                else
                {
                    item.Data = new byte[length];
                    Buffer.BlockCopy(payload, offset, item.Data, 0, length);
                    offset += length;
                }

                return item;
            }

            private IEnumerable<object> ScalarValues()
            {
                switch (Format)
                {
                    case 0x40:
                        yield return AsText();
                        yield break;
                    case 0x20:
                        yield return IsTextBytes(Data) && Data.Length > 1 ? (object)AsText() : (Data.Length == 0 ? 0 : Data[0]);
                        yield break;
                    case 0xA4:
                        foreach (var value in Data) yield return value;
                        yield break;
                    case 0xA8:
                        for (var i = 0; i + 1 < Data.Length; i += 2) yield return (ushort)((Data[i] << 8) | Data[i + 1]);
                        yield break;
                    case 0xB0:
                        for (var i = 0; i + 3 < Data.Length; i += 4) yield return ToUInt32(Data, i);
                        yield break;
                    case 0x90:
                        for (var i = 0; i + 3 < Data.Length; i += 4)
                        {
                            var bytes = Data.Skip(i).Take(4).Reverse().ToArray();
                            yield return BitConverter.ToSingle(bytes, 0);
                        }
                        yield break;
                    default:
                        yield return AsText();
                        yield break;
                }
            }
        }

        private static string ToSml(byte stream, byte function, bool wait, SecsItem item)
        {
            return "S" + stream.ToString(CultureInfo.InvariantCulture) + "F" + function.ToString(CultureInfo.InvariantCulture) + (wait ? " W " : " ") + (item == null ? "." : item.ToSml() + ".");
        }

        private static void WriteItemHeader(Stream stream, byte format, int length)
        {
            if (length <= 0xFF)
            {
                stream.WriteByte((byte)(format | 1));
                stream.WriteByte((byte)length);
                return;
            }

            stream.WriteByte((byte)(format | 2));
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)length);
        }

        private static string FormatName(byte format)
        {
            return format switch
            {
                0xA4 => "U1",
                0xA8 => "U2",
                0xB0 => "U4",
                0x90 => "F4",
                _ => "B",
            };
        }

        private static bool IsTextBytes(byte[] bytes)
        {
            return bytes != null && bytes.Length > 0 && bytes.All(x => x == 0 || x == 9 || x == 10 || x == 13 || (x >= 32 && x <= 126));
        }

        private static uint ToUInt32(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
