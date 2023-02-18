﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerLibrary;

public class CommandReader
{

    Stream stream;
    LineReader innerReader;

    Dictionary<string, Type> commandTypes;

    public CommandReader(Stream stream, Dictionary<string, Type> commandTypes)
    {
        this.stream = stream;
        this.commandTypes = commandTypes;

        this.innerReader = new LineReader(stream);
    }

    public async Task<Command> ReadCommandAsync()
    {

        ReadNext:

        string line = await this.innerReader.ReadLineAsync();

        string identifier = line.Split(new char[] { ' ' }, 2)[0];

        var commandType = this.commandTypes.FirstOrDefault(t => t.Key == identifier).Value;

        int errorCode;

        if (commandType == null && Int32.TryParse(identifier, out errorCode))
            commandType = typeof(ServerErrorCommand);

        if (commandType == null)
        {
            Debug.Assert(true, "Command of an unknown type: " + line);
            goto ReadNext; //gotos are bad
        }

        Command cmd = Activator.CreateInstance(commandType) as Command;

        await cmd.ReadAsync(line, this.innerReader);

        return cmd;

    }

    public void Close()
    {
        this.stream.Close();
    }

}

public static class CommandReaderExtensions
{
    public static IObservable<Command> GetReadObservable(this CommandReader reader)
    {

        return Observable.Create<Command>(async (observer, cancellationToken) =>
        {


            try
            {

                while (true)
                {

                    if (cancellationToken.IsCancellationRequested)
                    {
                        observer.OnCompleted();
                        return;
                    }

                    try
                    {
                        Command cmd = await reader.ReadCommandAsync();

                        observer.OnNext(cmd);
                    }
                    catch (Exception)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        throw;
                    }


                }

            }

            catch (Exception ex)
            {
                try
                {
                    observer.OnError(ex);
                }
                catch (Exception)
                {
                    Debug.Assert(true, "GetReadObservable - Exception thrown while calling OnError on observer. Check for any observers without error handlers.");
                    throw;
                }
            }

        });

    }



}

public class CommandWriter
{

    SemaphoreSlim @lock = new SemaphoreSlim(1);
    Stream stream;
    LineWriter innerWriter;

    public CommandWriter(Stream stream)
    {
        this.stream = stream;
        this.innerWriter = new LineWriter(stream);
    }

    public async Task WriteCommandAsync(Command command)
    {

        await this.@lock.WaitAsync();

        try
        {
            await command.WriteAsync(this.innerWriter);
        }
        finally
        {
            this.@lock.Release();
        }

    }

    public void Close()
    {
        this.stream.Close();
    }

}

public class ResponseTracker
{
    CommandWriter writer;
    IObservable<Command> commands;
    int trId;

    public ResponseTracker(CommandWriter writer, IObservable<Command> commands)
    {
        this.writer = writer;
        this.commands = commands;
    }

    public async Task<Command> GetResponseAsync(Command command, IEnumerable<Type> accepted, TimeSpan timeout)
    {

        command.TrId = Interlocked.Increment(ref this.trId);

        var getResponse = this.commands.Where(c => c.TrId == command.TrId)
                              .Where(c => c is ServerErrorCommand || accepted.Contains(c.GetType()))
                              .Timeout(timeout)
                              .FirstAsync()
                              .ToTask();

        await this.writer.WriteCommandAsync(command);

        var response = await getResponse;

        ServerErrorCommand error = response as ServerErrorCommand;

        if (error != null)
            throw new ServerErrorException(error.ErrorCode);

        return response;

    }

    public Task<Command> GetResponseAsync(Command command, TimeSpan timeout)
    {
        return this.GetResponseAsync(command, new Type[] { command.GetType() }, timeout);
    }

    public async Task<T> GetResponseAsync<T>(Command command, TimeSpan timeout) where T : Command
    {
        return (T)await this.GetResponseAsync(command, timeout);
    }


}


public abstract class Command
{

    public int? TrId { get; set; }

    public virtual Task ReadAsync(string header, LineReader reader)
    {
        throw new NotImplementedException();
    }

    public virtual Task WriteAsync(LineWriter writer)
    {
        throw new NotImplementedException();
    }




}


public class EnableIMCommand : Command
{

    //-> IMS 2 OFF
    //<- IMS 3 0 OFF

    public bool Enabled { get; set; }

    public EnableIMCommand()
    {
    }

    public EnableIMCommand(bool enable)
    {
        this.Enabled = enable;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');
        this.TrId = Int32.Parse(args[1]);
        this.Enabled = args[3] == "ON";

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("IMS {0} {1}", this.TrId, this.Enabled == true ? "ON" : "OFF");
    }

}

public class VersionCommand : Command
{

    //-> VER 1 MSNP12
    //<- VER 1 MSNP12

    public string[] Versions { get; set; }

    public VersionCommand()
    {
    }

    public VersionCommand(params string[] versions)
    {
        this.Versions = versions;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');
        this.TrId = Int32.Parse(args[1]);
        this.Versions = args.Skip(2).ToArray();
        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("VER {0} {1}", this.TrId, String.Join(" ", this.Versions));
    }

}

public class ClientVersionCommand : Command
{
    //-> CVR 2 0x0409 winnt 5.0 1386 MSMSGS 5.0.0482 WindowsMessenger dontshootthemsgr@hotmail.com
    //<- CVR 2 1.0.0000 1.0.0000 1.0.0000 http://msgr.dlservice.microsoft.com http://download.live.com/?sku=messenger

    public string LocaleId { get; set; }
    public string OsType { get; set; }
    public string OsVersion { get; set; }
    public string Architecture { get; set; }
    public string LibraryName { get; set; }
    public string ClientVersion { get; set; }
    public string ClientName { get; set; }
    public string LoginName { get; set; }

    public ClientVersionCommand()
    {
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');
        this.TrId = Int32.Parse(args[1]);
        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("CVR {0} {1} {2} {3} {4} {5} {6} {7} {8}", this.TrId, this.LocaleId, this.OsType, this.OsVersion, this.Architecture, this.LibraryName, this.ClientVersion, this.ClientName, this.LoginName);
    }

}

public class AuthenticateCommand : Command
{

    //-> USR 3 TWN I dontshootthemsgr@hotmail.com
    //<- USR 3 TWN S ct=1364480777,rver=6.2.6289.0,wp=FS_40SEC_0_COMPACT,lc=1033,id=507,ru=http:%2F%2Fmessenger.msn.com,tw=0,kpp=1,kv=4,ver=2.1.6000.1,rn=1lgjBfIL,tpf=b0735e3a873dfb5e75054465196398e0

    //-> USR 4 TWN S t=EwDwAfsBAAAUW9%2b7Miyt3HjZIRD38NPpViMVcoOAAKGxphH8eUidQ2zxnVQoscCCq1OCFmkTOBqp4T1xCL47/KElJ0dz3odVnl7PeIkDYMRt%2b5vn5o0n%2bXtztDe1X1jXaMR0WkbDz2fL7FeipGUhoHl5ywF%2bjGP1p7xvM0lvlnM6xvN9go%2b%2bi3GONRhkbMCEtaHi2xJzIT0kmynIAuRVA2YAAAiAZfjVg5xT1UABmdfcMjc7q6WAgZtVK%2bOJzvGGnwDpHMeMJklvTBo9Snd/7PNv808%2bfkNEVgRpg5/QgmLAdnn3B5T5Ah7rWpr8Bi7rocZk/jUu9mBxxo%2b2XkkpkqwROuj87IezECnSV9AWqOJwrcAOeBF6d9ySTxBeQKzN2jJv%2bmq5uqM%2b2TCA9ZigZFrttP8JImAYa2pRFKn0HGYsPBDKJ9YAthW3jSTtMd7swdePjX/bP1KPeYuDIrng6sfc%2bz%2bpkRFtFDNpso96/3ljk%2b2okn4oSlhKg21yIFdtj0yD67RDXXgBzDDK0k5cpv8aZYbt7d66lgxRzQJmfXSlf7XFo4hl3X86y20qlI/cIrF%2bOpfe6WP/bOYKG9eEdA0coPD7vu7R1mAg6Nizrc2elr14vjTWoAPMEr5wwViKwF7VWooTTQJaUezFMk12AQ%3d%3d&p=
    //<- USR 4 OK dontshootthemsgr@hotmail.com 1 0

    public string AuthType { get; set; }
    public string Status { get; set; }
    public string Argument { get; set; }

    public AuthenticateCommand()
    {
    }

    public AuthenticateCommand(string method, string phase, string argument)
    {
        this.AuthType = method;
        this.Status = phase;
        this.Argument = argument;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.AuthType = args[2];
        this.Status = args[3];
        this.Argument = args[4];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("USR {0} {1} {2} {3}", this.TrId, this.AuthType, this.Status, this.Argument);
    }

}

public class TransferCommand : Command
{

    //<- XFR 3 NS 64.4.61.38:1863 0 64.4.45.62:1863

    //-> XFR 15 SB
    //<- XFR 15 SB 207.46.108.37:1863 CKI 17262740.1050826919.32308

    public string ServerType { get; set; }
    public string Host { get; set; }
    public string AuthType { get; set; }
    public string Host2OrSessionID { get; set; }

    public TransferCommand()
    {
    }

    public TransferCommand(string serverType)
    {
        this.ServerType = serverType;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.ServerType = args[2];
        this.Host = args[3];
        this.AuthType = args[4];
        this.Host2OrSessionID = args[5];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("XFR {0} {1}", this.TrId, this.ServerType);
    }

}

public class SynchronizeCommand : Command
{

    //-> SYN 5 0 0
    //<- SYN 5 2013-01-13T05:15:19.637-08:00 2012-09-23T06:51:31.243-07:00 14 3

    public string TimeStamp1 { get; set; }
    public string TimeStamp2 { get; set; }
    public int UserCount { get; set; }
    public int GroupCount { get; set; }

    public SynchronizeCommand()
    {
    }

    public SynchronizeCommand(string timeStamp1, string timeStamp2)
    {
        this.TimeStamp1 = timeStamp1;
        this.TimeStamp2 = timeStamp2;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.TimeStamp1 = args[2];
        this.TimeStamp2 = args[3];

        if (args.Length > 4)
        {
            this.UserCount = Int32.Parse(args[4]);
            this.GroupCount = Int32.Parse(args[5]);
        }

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("SYN {0} {1} {2}", this.TrId, this.TimeStamp1, this.TimeStamp2);
    }

}

public class LocalPropertyCommand : Command
{

    //<- PRP MFN Test (when logging in)
    //<- PRP 1 MFN Hello%20Joe
    public string Key { get; set; }
    public string Value { get; set; }

    public LocalPropertyCommand()
    {
    }

    public LocalPropertyCommand(string key, string value)
    {
        this.Key = key;
        this.Value = value;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        int offset = 0;

        if (args.Length > 3)
        {
            offset = 1;
            this.TrId = Int32.Parse(args[1]);
        }

        this.Key = args[1 + offset];
        this.Value = Uri.UnescapeDataString(args[2 + offset]);

        return Task.FromResult<object>(null);

    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("PRP {0} {1} {2}", this.TrId, this.Key, Uri.EscapeDataString(this.Value));
    }

}

public class UserPropertyCommand : Command
{
    //<- BPR HSB 1
    public string Key { get; set; }
    public string Value { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.Key = args[1];
        this.Value = Uri.UnescapeDataString(args[2]);

        return Task.FromResult<object>(null);
    }

}

public class ChangeStatusCommand : Command
{

    //-> CHG 6 NLN 0
    //<- CHG 6 NLN 0

    //-> CHG 8 AWY 2684354564
    //<- CHG 8 AWY 2684354564

    //-> CHG 8 NLN 2684354564 %3cmsnobj%20Creator%3d%22boo%40hotmail.com%22%20Size%3d%2214239%22%20Type%3d%223%22%20Location%3d%22TFR70102DFF.tmp%22%20Friendly%3d%22AAA%3d%22%20SHA1D%3d%22pp5heJu6yL9ZOIReu5Sz9UcFDr8%3d%22%20SHA1C%3d%22hLVSyHICzsvB2yzezdkPDBA%2b%2fzc%3d%22%2f%3e
    //<- CHG 8 NLN 2684354564 %3cmsnobj%20Creator%3d%22boo%40hotmail.com%22%20Size%3d%2214239%22%20Type%3d%223%22%20Location%3d%22TFR70102DFF.tmp%22%20Friendly%3d%22AAA%3d%22%20SHA1D%3d%22pp5heJu6yL9ZOIReu5Sz9UcFDr8%3d%22%20SHA1C%3d%22hLVSyHICzsvB2yzezdkPDBA%2b%2fzc%3d%22%2f%3e

    public string Status { get; set; }
    public uint Capabilities { get; set; }
    public string DisplayPicture { get; set; }

    public ChangeStatusCommand()
    {
    }

    public ChangeStatusCommand(string status, UInt32 capabilities, string displayPicture)
    {
        this.Status = status;
        this.Capabilities = capabilities;
        this.DisplayPicture = displayPicture;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.Status = args[2];
        this.Capabilities = UInt32.Parse(args[3]);

        if (args.Length > 4)
            this.DisplayPicture = args[4];

        return Task.FromResult<object>(null);

    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("CHG {0} {1} {2} {3}", this.TrId, this.Status, this.Capabilities, this.DisplayPicture);
    }


}

public class ChangeUserPropertyCommand : Command
{

    //-> SBP 8 af54c348-455a-479a-9964-cbb48232c3c9 MFN newnickname
    //<- SBP 8 af54c348-455a-479a-9964-cbb48232c3c9 MFN newnickname

    public string Key { get; set; }
    public string Value { get; set; }
    public string Guid { get; set; }

    public ChangeUserPropertyCommand()
    {
    }

    public ChangeUserPropertyCommand(string guid, string key, string value)
    {
        this.Guid = guid;
        this.Key = key;
        this.Value = value;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.Guid = args[2];
        this.Key = args[3];
        this.Value = Uri.UnescapeDataString(args[4]);

        return Task.FromResult<object>(null);

    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("SBP {0} {1} {2} {3}", this.TrId, this.Guid, this.Key, Uri.EscapeDataString(this.Value));
    }

}

public class AcknowledgementCommand : Command
{

    //<- ACK 1

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');
        this.TrId = Int32.Parse(args[1]);
        return Task.FromResult<object>(null);
    }

}

public class NegativeAcknowledgementCommand : Command
{

    //<- NAK 1

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');
        this.TrId = Int32.Parse(args[1]);
        return Task.FromResult<object>(null);
    }

}

public class SendMessageCommand : Command
{

    //-> MSG 3 A 170
    //-> (170 bytes)

    public string DeliveryMethod { get; set; }
    public int PayloadLength { get; set; }
    public byte[] Payload { get; set; }

    public SendMessageCommand(string deliveryMethod, byte[] payload)
    {
        this.DeliveryMethod = deliveryMethod;
        this.Payload = payload;
        this.PayloadLength = payload.Length;
    }

    public override async Task WriteAsync(LineWriter writer)
    {
        await writer.WriteLineAsync("MSG {0} {1} {2}", this.TrId ?? 0, this.DeliveryMethod, this.PayloadLength);

        await writer.WriteAsync(this.Payload, 0, this.PayloadLength);

    }


}

public class MessageCommand : Command
{

    //<- MSG zues@hotmail.com nickname 135
    //<- (135 bytes)

    public string Sender { get; set; }
    public string SenderNickname { get; set; }
    public int PayloadLength { get; set; }
    public byte[] Payload { get; set; }

    public async override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.Sender = args[1];
        this.SenderNickname = args[2];
        this.PayloadLength = Int32.Parse(args[3]);
        this.Payload = new byte[this.PayloadLength];

        await reader.ReadAsync(this.Payload, 0, this.PayloadLength);

    }


}

public class BroadcastCommand : Command
{

    //<- UBX alice@worldofalice.com 471
    //<- (471 bytes)

    public string LoginName { get; set; }
    public string Message { get; set; }

    public async override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.LoginName = args[1];
        int payloadLength = Int32.Parse(args[2]);
        byte[] payload = new byte[payloadLength];

        await reader.ReadAsync(payload, 0, payloadLength);

        this.Message = UTF8Encoding.UTF8.GetString(payload);

    }


}

public class NotificationCommand : Command
{


    //<- NOT 300
    //<- (300 bytes)

    public string Message { get; set; }

    public async override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        int payloadLength = Int32.Parse(args[1]);
        byte[] payload = new byte[payloadLength];

        await reader.ReadAsync(payload, 0, payloadLength);

        this.Message = UTF8Encoding.UTF8.GetString(payload);

    }


}

public class UserCommand : Command
{

    //<- LST N=jiggar@live.com F=JigWig C=8b2d1a22-0000-0000-0000-000000000000 3 1
    //<- LST N=goggle@hotmail.com 2 1
    //<- LST N=alice@worldofalice.com F=(i)Alice%20-%20(au)%20Type%20Race%20and%20play%20with%20me! C=c3f922f5-1381-485e-b17e-21eefd2ce118 3 1
    //<- LST N=bigboy@hotmail.com F=bigboy@hotmail.com C=7f4fe7f7-8801-4bf5-8609-48f30ef7d6a9 5 1 d673f5f8-59ca-40fb-868c-51d34cf0d7dc
    //<- LST N=bigboy@hotmail.com F=bigboy@hotmail.com C=7f4fe7f7-8801-4bf5-8609-48f30ef7d6a9 5 1 d673f5f8-59ca-40fb-868c-51d34cf0d7dc,222222-59ca-40fb-868c-51d34cf0d7dc

    public string LoginName { get; set; }
    public string Nickname { get; set; }
    public string Guid { get; set; }
    public string[] Lists { get; set; }
    public string[] Groups { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        List<string> args = new List<string>();
        Dictionary<string, string> keyValues = new Dictionary<string, string>();

        foreach (string arg in header.Split(' ').Skip(1))
        {
            var spl = arg.Split(new char[] {'='}, 2);

            if (spl.Length == 2)
                keyValues.Add(spl[0], spl[1]);
            else
                args.Add(arg);

        }

        this.LoginName = keyValues["N"];

        if (keyValues.ContainsKey("F"))
            this.Nickname = Uri.UnescapeDataString(keyValues["F"]);

        if (keyValues.ContainsKey("C"))
            this.Guid = keyValues["C"];

        int listFlags = Int32.Parse(args[0]);

        var lists = new Dictionary<int, string> {
            { 1, "FL" },
            { 2, "AL" },
            { 4, "BL" },
            { 8, "RL" },
            { 16, "PL" },
        };

        this.Lists = lists.Where(x => (listFlags & x.Key) == x.Key).Select(x => x.Value).ToArray();

        if (args.Count > 2)
            this.Groups = args[2].Split(',');
        else
            this.Groups = new string[0];

        return Task.FromResult<object>(null);
    }



}

public class GroupCommand : Command
{

    //<- LSG asdasdasd 9bbc774a-dc40-413d-829c-07c2a3356e01

    public string Name { get; set; }
    public string Guid { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.Name = Uri.UnescapeDataString(args[1]);
        this.Guid = args[2];

        return Task.FromResult<object>(null);
    }



}

public class PrivacySettingCommand : Command
{
    //<- BLP BL
    //<- GTC A

    public string Key { get; set; }
    public string Value { get; set; }

    public PrivacySettingCommand()
    {
    }

    public PrivacySettingCommand(string key, string value)
    {
        this.Key = key;
        this.Value = value;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        if (args.Length == 2)
        {
            this.Key = args[0];
            this.Value = args[1];
        }
        else
        {
            this.Key = args[0];
            this.TrId = Int32.Parse(args[1]);
            this.Value = args[2];
        }

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("{0} {1} {2}", this.Key, this.TrId, this.Value);
    }

}

public class SbsCommand : Command
{
    //<- SBS 0 null

    public string Arg1 { get; set; }
    public string Arg2 { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.Arg1 = args[1];
        this.Arg2 = args[2];

        return Task.FromResult<object>(null);
    }

}

public class OutCommand : Command
{

    //<- OUT OTH

    public string OutCode { get; set; }


    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.OutCode = args[1];

        return Task.FromResult<object>(null);

    }

}

public class RingCommand : Command
{

    //<- RNG 11752013 207.46.108.38:1863 CKI 849102291.520491113 example@passport.com Example%20Name

    public string Caller { get; set; }
    public string CallerNickname { get; set; }
    public string Endpoint { get; set; }
    public string SessionId { get; set; }
    public string AuthString { get; set; }
    public string AuthType { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {


        string[] args = header.Split(' ');

        this.SessionId = args[1];
        this.Endpoint = args[2];
        this.AuthType = args[3];
        this.AuthString = args[4];
        this.Caller = args[5];
        this.CallerNickname = Uri.UnescapeDataString(args[6]);

        return Task.FromResult<object>(null);

    }



}

public class ChallengeCommand : Command
{

    //<- CHL 0 15570131571988941333

    public string ChallengeString { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.ChallengeString = args[2];

        return Task.FromResult<object>(null);

    }



}

public class AcceptChallengeCommand : Command
{

    //-> QRY 1049 msmsgs@msnmsgr.com 32
    //-> (32 bytes)
    //<- QRY 1049

    public string ClientID { get; set; }
    public string Data { get; set; }

    public AcceptChallengeCommand()
    {
    }

    public AcceptChallengeCommand(string clientID, string data)
    {
        this.ClientID = clientID;
        this.Data = data;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);

        return Task.FromResult<object>(null);

    }

    public override async Task WriteAsync(LineWriter writer)
    {

        byte[] payload = UTF8Encoding.UTF8.GetBytes(this.Data);

        await writer.WriteLineAsync("QRY {0} {1} {2}", this.TrId, this.ClientID, payload.Length);

        await writer.WriteAsync(payload, 0, payload.Length);

    }


}

public abstract class UserOnlineCommandBase : Command
{
    public string LoginName { get; set; }
    public string Status { get; set; }
    public string Nickname { get; set; }
    public uint Capabilities { get; set; }
    public string DisplayPicture { get; set; }
}

public class UserOnlineCommand : UserOnlineCommandBase
{
    //<- NLN NLN alice@worldofalice.com (i)Alice%20-%20(au)%20Type%20Race%20and%20play%20with%20me! 1879474220
    //<- NLN NLN alice@worldofalice.com (i)Alice%20-%20(au)%20Type%20Race%20and%20play%20with%20me! 1879474220 %3cmsnobj%20Creator%3d%22alice%40worldofalice.com%22%20Size%3d%2217662%22%20Type%3d%223%22%20Location%3d%220%22%20Friendly%3d%22AAA%3d%22%20SHA1D%3d%22ww6tgOK0b4OTI64m044RtuUUFoI%3d%22%20SHA1C%3d%22Zd8Fd5jk%2bhWR9w9LfyiGUD%2bH88I%3d%22%2f%3e

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.Status = args[1];
        this.LoginName = args[2];
        this.Nickname = Uri.UnescapeDataString(args[3]);
        this.Capabilities = UInt32.Parse(args[4]);

        if (args.Length > 5)
            this.DisplayPicture = args[5];

        return Task.FromResult<object>(null);

    }



}

public class InitialUserOnlineCommand : UserOnlineCommandBase
{

    //<- ILN 0 NLN zool@hotmail.com jo 1345323044
    //<- ILN 0 NLN zool@hotmail.com jo 1345323044 %3cmsnobj%20Creator%3d%22boo%40hotmail.com%22%20Size%3d%2214239%22%20Type%3d%223%22%20Location%3d%22TFR4E0B1838.tmp%22%20Friendly%3d%22AAA%3d%22%20SHA1D%3d%22pp5heJu6yL9ZOIReu5Sz9UcFDr8%3d%22%20SHA1C%3d%22hLVSyHICzsvB2yzezdkPDBA%2b%2fzc%3d%22%2f%3e

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.Status = args[2];
        this.LoginName = args[3];
        this.Nickname = Uri.UnescapeDataString(args[4]);
        this.Capabilities = UInt32.Parse(args[5]);

        if (args.Length > 6)
            this.DisplayPicture = args[6];

        return Task.FromResult<object>(null);

    }



}

public class UserOfflineCommand : Command
{

    //<- FLN 20 zool@hotmail.com

    public string LoginName { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.LoginName = args[1];

        return Task.FromResult<object>(null);

    }



}

public class PingCommand : Command
{

    //-> PNG
    //<- QNG 30

    public int UntilNext { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.UntilNext = Int32.Parse(args[1]);

        return Task.FromResult<object>(null);

    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("PNG");
    }


}

public class SendBroadcastCommand : Command
{

    //-> UUX 7 58
    //-> (58 bytes)
    //<- UUX 7 0

    public string Message { get; set; }
    public string OtherArg { get; set; }

    public SendBroadcastCommand()
    {
    }

    public SendBroadcastCommand(string message)
    {
        this.Message = message;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.OtherArg = args[2];

        return Task.FromResult<object>(null);

    }

    public override async Task WriteAsync(LineWriter writer)
    {

        byte[] payload = UTF8Encoding.UTF8.GetBytes(this.Message);

        await writer.WriteLineAsync("UUX {0} {1}", this.TrId, payload.Length);

        await writer.WriteAsync(payload, 0, payload.Length);

    }


}

public class UserRosterCommand : Command
{

    //<- IRO 1 1 2 myname@msn.com My%20Name
    //<- IRO 1 2 2 somename@msn.com OtherName
    public string LoginName { get; set; }
    public string NickName { get; set; }
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.CurrentIndex = Int32.Parse(args[2]);
        this.TotalCount = Int32.Parse(args[3]);
        this.LoginName = args[4];
        this.NickName = Uri.UnescapeDataString(args[5]);

        return Task.FromResult<object>(null);

    }



}

public class UserJoinedCommand : Command
{

    //<- JOI dave@passport.com Dave
    public string LoginName { get; set; }
    public string NickName { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.LoginName = args[1];
        this.NickName = Uri.UnescapeDataString(args[2]);

        return Task.FromResult<object>(null);

    }



}

public class UserPartedCommand : Command
{

    //<- BYE dave@passport.com
    //<- BYE dave@passport.com 1

    public string LoginName { get; set; }
    public string NickName { get; set; }
    public bool DueToInactivity { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.LoginName = args[1];

        if (args.Length > 2)
            this.DueToInactivity = args[2] == "1";

        return Task.FromResult<object>(null);

    }



}

public class AnswerCommand : Command
{

    //-> ANS 1 name_123@hotmail.com 849102291.520491113 11752013
    //<- ANS 1 OK

    public string LoginName { get; set; }
    public string AuthToken { get; set; }
    public string SessionID { get; set; }
    public string OtherArg { get; set; }

    public AnswerCommand()
    {
    }

    public AnswerCommand(string loginName, string authToken, string sessionID)
    {
        this.LoginName = loginName;
        this.AuthToken = authToken;
        this.SessionID = sessionID;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.OtherArg = args[2];

        return Task.FromResult<object>(null);

    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("ANS {0} {1} {2} {3}", this.TrId, this.LoginName, this.AuthToken, this.SessionID);
    }

}

public class AuthenticateIMCommand : Command
{

    //-> USR 1 example@passport.com 17262740.1050826919.32308
    //<- USR 1 OK example@passport.com Example%20Name

    public string LoginName { get; set; }
    public string Nickname { get; set; }
    public string SessionID { get; set; }
    public string OtherArg { get; set; }


    public AuthenticateIMCommand()
    {
    }

    public AuthenticateIMCommand(string loginName, string sessionID)
    {
        this.LoginName = loginName;
        this.SessionID = sessionID;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.OtherArg = args[2];
        this.LoginName = args[3];
        this.Nickname = Uri.UnescapeDataString(args[4]);

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("USR {0} {1} {2}", this.TrId, this.LoginName, this.SessionID);
    }

}

public class CallUserCommand : Command
{

    //-> CAL 2 name_123@hotmail.com
    //<- CAL 2 RINGING 11752013

    public string LoginName { get; set; }
    public string Response { get; set; }
    public string SessionID { get; set; }

    public CallUserCommand()
    {
    }

    public CallUserCommand(string loginName)
    {
        this.LoginName = loginName;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.Response = args[2];
        this.SessionID = args[3];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("CAL {0} {1}", this.TrId, this.LoginName);
    }

}

public class AddGroupCommand : Command
{

    //-> ADG 7 bllala
    //<- ADG 7 bllala fe0117b8-bb49-430b-8550-83e2fdfd8f86

    public string Name { get; set; }
    public string Guid { get; set; }

    public AddGroupCommand()
    {
    }

    public AddGroupCommand(string name)
    {
        this.Name = name;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.Name = Uri.UnescapeDataString(args[2]);
        this.Guid = args[3];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("ADG {0} {1}", this.TrId, Uri.EscapeDataString(this.Name));
    }

}

public class RemoveGroupCommand : Command
{
    //-> RMG 8 056de1aa-9083-44a3-9d06-160121fe743a
    //<- RMG 8 056de1aa-9083-44a3-9d06-160121fe743a

    public string Guid { get; set; }

    public RemoveGroupCommand()
    {
    }

    public RemoveGroupCommand(string guid)
    {
        this.Guid = guid;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.Guid = args[2];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("RMG {0} {1}", this.TrId, this.Guid);
    }

}

public class RenameGroupCommand : Command
{

    //-> REG 10 9f55493b-b548-44ae-b125-44801ab4bc67 smama
    //<- REG 10 9f55493b-b548-44ae-b125-44801ab4bc67 smama

    public string Name { get; set; }
    public string Guid { get; set; }

    public RenameGroupCommand()
    {
    }

    public RenameGroupCommand(string guid, string name)
    {
        this.Guid = guid;
        this.Name = name;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {
        return writer.WriteLineAsync("REG {0} {1} {2}", this.TrId, this.Guid, Uri.EscapeDataString(this.Name));
    }

}

public class AddContactCommand : Command
{

    //add user to forward list
    //-> ADC 7 FL N=blimp@hotmail.com F=blimp%40hotmail.com
    //<- ADC 7 FL N=blimp@hotmail.com F=blimp@hotmail.com C=2a497286-0000-0000-0000-000000000000

    //add user to group
    //-> ADC 8 FL C=2a497286-0000-0000-0000-000000000000 9f55493b-b548-44ae-b125-44801ab4bc67
    //<- ADC 8 FL C=2a497286-0000-0000-0000-000000000000 9f55493b-b548-44ae-b125-44801ab4bc67

    //add user to block list
    //-> ADC 9 BL N=blimp@hotmail.com
    //<- ADC 9 BL N=blimp@hotmail.com

    public string List { get; set; }
    public string LoginName { get; set; }
    public string Nickname { get; set; }
    public string Guid { get; set; }
    public string GroupGuid { get; set; }

    public AddContactCommand()
    {
    }

    //for adding users to groups
    public AddContactCommand(string list, string guid, string nickname, string groupGuid)
    {
        this.List = list;
        this.Guid = guid;
        this.Nickname = nickname;
        this.GroupGuid = groupGuid;
    }

    //for adding a user to a list
    public AddContactCommand(string list, string loginName, string nickname)
    {
        this.List = list;
        this.LoginName = loginName;
        this.Nickname = nickname;
    }

    public AddContactCommand(string list, string loginName, string guid, string nickname, string groupGuid)
    {
        this.List = list;
        this.LoginName = loginName;
        this.Guid = guid;
        this.Nickname = nickname;
        this.GroupGuid = groupGuid;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {

        List<string> args = new List<string>();
        Dictionary<string, string> keyValues = new Dictionary<string, string>();

        foreach (string arg in header.Split(' '))
        {
            var spl = arg.Split('=');

            if (spl.Length == 2)
                keyValues.Add(spl[0], spl[1]);
            else
                args.Add(arg);

        }

        this.TrId = Int32.Parse(args[1]);
        this.List = args[2];

        if (keyValues.ContainsKey("N"))
            this.LoginName = keyValues["N"];

        if (keyValues.ContainsKey("C"))
            this.Guid = keyValues["C"];

        if (keyValues.ContainsKey("F"))
            this.Nickname = Uri.UnescapeDataString(keyValues["F"]);

        if (args.Count > 3)
            this.GroupGuid = args[3];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {

        StringBuilder sb = new StringBuilder();

        sb.AppendFormat("ADC {0} {1}", this.TrId, this.List);

        if (this.LoginName != null)
            sb.Append(" ").AppendFormat("N={0}", this.LoginName);

        if (this.Guid != null)
            sb.Append(" ").AppendFormat("C={0}", this.Guid);

        if (this.Nickname != null)
            sb.Append(" ").AppendFormat("F={0}", Uri.EscapeDataString(this.Nickname));

        if (this.GroupGuid != null)
            sb.Append(" ").Append(this.GroupGuid);

        return writer.WriteLineAsync(sb.ToString());

    }

}

public class RemoveContactCommand : Command
{

    //remove user from group
    //-> REM 11 FL 2a497286-0000-0000-0000-000000000000 9f55493b-b548-44ae-b125-44801ab4bc67
    //<- REM 11 FL 2a497286-0000-0000-0000-000000000000 9f55493b-b548-44ae-b125-44801ab4bc67

    //remove user from forward list
    //-> REM 13 FL 2a497286-0000-0000-0000-000000000000
    //<- REM 13 FL 2a497286-0000-0000-0000-000000000000

    //remove user from block list
    //-> REM 12 BL blimp@hotmail.com
    //<- REM 12 BL blimp@hotmail.com

    public string List { get; set; }
    public string LoginNameOrGuid { get; set; }
    public string GroupGuid { get; set; }

    public RemoveContactCommand()
    {
    }

    public RemoveContactCommand(string list, string loginNameOrGuid, string groupGuid)
    {
        this.List = list;
        this.LoginNameOrGuid = loginNameOrGuid;
        this.GroupGuid = groupGuid;
    }

    public RemoveContactCommand(string list, string loginNameOrGuid)
    {
        this.List = list;
        this.LoginNameOrGuid = loginNameOrGuid;
    }

    public override Task ReadAsync(string header, LineReader reader)
    {
        string[] args = header.Split(' ');

        this.TrId = Int32.Parse(args[1]);
        this.List = args[2];
        this.LoginNameOrGuid = args[3];

        if (args.Length > 4)
            this.GroupGuid = args[4];

        return Task.FromResult<object>(null);
    }

    public override Task WriteAsync(LineWriter writer)
    {

        StringBuilder sb = new StringBuilder();

        sb.AppendFormat("REM {0} {1} {2}", this.TrId, this.List, this.LoginNameOrGuid);

        if (this.GroupGuid != null)
            sb.Append(" ").Append(this.GroupGuid);

        return writer.WriteLineAsync(sb.ToString());
    }

}

public class ServerErrorCommand : Command
{

    //<< 202 1

    public int ErrorCode { get; set; }

    public override Task ReadAsync(string header, LineReader reader)
    {

        string[] args = header.Split(' ');

        this.ErrorCode = Int32.Parse(args[0]);
        this.TrId = Int32.Parse(args[1]);

        return Task.FromResult<object>(null);

    }

}