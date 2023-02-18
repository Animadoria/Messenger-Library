using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerLibrary;

public interface IConnection : IDisposable
{
    Task ConnectAsync(IEndPoint remoteEP);
    Task SendAsync(byte[] buffer, int offset, int size);
    Task<int> ReceiveAsync(byte[] buffer, int offset, int size);
    void Close();
    event EventHandler<ConnectionErrorEventArgs> Error;
}

public interface IEndPoint
{

}

public class ConnectionStream : Stream
{
    private readonly IConnection connection;

    public ConnectionStream(IConnection connection)
    {
        this.connection = connection;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.connection.SendAsync(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.connection.ReceiveAsync(buffer, offset, count);
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotImplementedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void Flush()
    {
        throw new NotImplementedException();
    }

    public override long Position
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }
}

public abstract class ConnectionErrorException : Exception
{

    internal ConnectionErrorException(string message)
        : base(message)
    {
    }

    internal ConnectionErrorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

}

public class ConnectionErrorEventArgs : EventArgs
{

    internal ConnectionErrorException Exception { get; private set; }

    internal ConnectionErrorEventArgs(ConnectionErrorException exception)
    {
        this.Exception = exception;
    }
}

public class SocketConnection : IConnection
{

    Socket socket;
    int eventRaised;
    bool closed;

    public async Task ConnectAsync(IEndPoint remoteEP)
    {

        SocketEndPoint socketEndPoint = remoteEP as SocketEndPoint;

        if (socketEndPoint == null)
            throw new ArgumentException("remoteEP must be of type SocketEndPoint", "remoteEP");

        this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await Task.Factory.FromAsync(this.socket.BeginConnect(socketEndPoint.Address, socketEndPoint.Port, null, null), this.socket.EndConnect);
        }
        catch (SocketException ex)
        {
            throw new SocketConnectionException(ex);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(this.GetType().Name);
        }

    }

    public async Task SendAsync(byte[] data, int offset, int size)
    {

        try
        {

            int remains = size;

            while (remains > 0)
            {

                int sent = await Task.Factory.FromAsync<int>(this.socket.BeginSend(data, offset + (size - remains), remains, SocketFlags.None, null, null), this.socket.EndSend);

                if (sent == 0)
                    throw new SocketConnectionException("Unexpected disconnect");

                remains -= size;

            }

        }
        catch (SocketException ex)
        {

            ConnectionErrorException connectionException = new SocketConnectionException(ex);

            if (Interlocked.Exchange(ref this.eventRaised, 1) == 0)
                this.OnError(new ConnectionErrorEventArgs(connectionException));

            throw new SocketConnectionException(ex);

        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(this.GetType().Name);
        }


    }

    public async Task<int> ReceiveAsync(byte[] data, int offset, int size)
    {

        try
        {

            int received = await Task.Factory.FromAsync<int>(this.socket.BeginReceive(data, offset, size, SocketFlags.None, null, null), this.socket.EndReceive);

            if (received == 0)
                throw new SocketConnectionException("Unexpected disconnect");

            return received;

        }
        catch (SocketException ex)
        {

            ConnectionErrorException connectionException = new SocketConnectionException(ex);

            if (Interlocked.Exchange(ref this.eventRaised, 1) == 0)
                this.OnError(new ConnectionErrorEventArgs(connectionException));

            throw new SocketConnectionException(ex);

        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(this.GetType().Name);
        }


    }

    public void Close()
    {

        if (this.closed)
            return;

        this.closed = true;

        try
        {
            this.socket.Shutdown(SocketShutdown.Both);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        this.socket.Close();

    }

    void IDisposable.Dispose()
    {
        this.Close();
    }

    public event EventHandler<ConnectionErrorEventArgs> Error;

    void OnError(ConnectionErrorEventArgs e)
    {
        EventHandler<ConnectionErrorEventArgs> handler = this.Error;
        if (handler != null) handler(this, e);
    }

}

public class SocketEndPoint : IEndPoint
{

    public string Address { get; private set; }
    public int Port { get; private set; }

    SocketEndPoint(string address, int port)
    {
        this.Address = address;
        this.Port = port;
    }

    public override string ToString()
    {
        return string.Format("{0}:{1}", this.Address, this.Port);
    }

    public static SocketEndPoint Parse(string s)
    {
        string[] parts = s.Split(':');
        return new SocketEndPoint(parts[0], int.Parse(parts[1]));
    }

}

public class SocketConnectionException : ConnectionErrorException
{

    internal SocketConnectionException(string message)
        : base(message)
    {
    }

    internal SocketConnectionException(SocketException innerException)
        : base("Underlying socket error: " + innerException.Message, innerException)
    {
    }


}