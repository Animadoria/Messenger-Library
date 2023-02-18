using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MessengerLibrary;

public class LineWriter
{

    Stream stream;

    public LineWriter(Stream stream)
    {
        this.stream = stream;
    }

    public async Task WriteLineAsync(string line)
    {

        Debug.WriteLine("-> " + line);

        byte[] data = UTF8Encoding.UTF8.GetBytes(line + "\r\n");

        await this.stream.WriteAsync(data, 0, data.Length);

    }

    public Task WriteLineAsync(string format, params object[] arg)
    {
        return this.WriteLineAsync(String.Format(format, arg));
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count)
    {

        Debug.WriteLine("-> ({0} bytes)", count);

        await this.stream.WriteAsync(buffer, offset, count);

    }

}

public class LineReader
{
    Stream stream;
    byte[] incoming;
    byte[] buffer;
    int cursor;
    int readSize = 1024;

    public LineReader(Stream stream)
    {
        this.stream = stream;
        this.buffer = new byte[0];
    }

    public async Task<bool> ReadAsync(byte[] buffer, int offset, int count)
    {

        while (true)
        {

            if (this.TakeFromBuffer(buffer, offset, count))
            {
                Debug.WriteLine("<- ({0} bytes)", count);

                return true;
            }

            if (await this.GetMoreData() == false)
                return false;

        }

    }

    public bool TakeFromBuffer(byte[] dst, int dstOffset, int count)
    {

        if (this.buffer.Length >= count)
        {

            Buffer.BlockCopy(this.buffer, 0, dst, dstOffset, count);

            byte[] newBuffer = new byte[this.buffer.Length - count];
            Buffer.BlockCopy(this.buffer, count, newBuffer, dstOffset, this.buffer.Length - count);
            this.buffer = newBuffer;

            return true;

        }

        return false;

    }

    public async Task<string> ReadLineAsync()
    {
 
        while (true)
        {
            string line = this.GetLineFromBuffer();

            if (line != null)
            {
                Debug.WriteLine("<- " + line);

                return line;
            }

            if (await this.GetMoreData() == false)
                return null;

        }



    }

    internal string GetLineFromBuffer()
    {

        int cr;

        while (true)
        {

            cr = Array.IndexOf(this.buffer, (byte)'\r', this.cursor);

            if (cr == -1)
            {
                this.cursor = this.buffer.Length;
                return null;
            }

            if (this.buffer.Length < (cr + 2))
            {
                this.cursor = cr;
                return null;
            }

            if (this.buffer[cr + 1] == (byte)'\n')
                break;

            this.cursor = cr + 1;

        }

        byte[] line = new byte[cr];
        Buffer.BlockCopy(this.buffer, 0, line, 0, cr);

        byte[] newBuffer = new byte[this.buffer.Length - (cr + 2)];
        Buffer.BlockCopy(this.buffer, cr + 2, newBuffer, 0, this.buffer.Length - (cr + 2));
        this.buffer = newBuffer;

        this.cursor = 0;

        return Encoding.UTF8.GetString(line);

    }

    internal async Task<bool> GetMoreData()
    {
        this.incoming = new byte[this.readSize];

        int len = await this.stream.ReadAsync(this.incoming, 0, this.readSize);

        if (len == 0)
            return false;

        Array.Resize<byte>(ref this.buffer, this.buffer.Length + len);
        Buffer.BlockCopy(this.incoming, 0, this.buffer, this.buffer.Length - len, len);

        this.incoming = null;

        return true;

    }


}