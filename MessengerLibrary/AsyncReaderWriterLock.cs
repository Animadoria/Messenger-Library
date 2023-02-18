using System;
using System.Collections.Generic;
using System.Threading.Tasks;

//credit for ReaderWriterAsync goes to Stephen Toub from Microsoft - thanks!
//http://blogs.msdn.com/b/pfxteam/archive/2012/02/12/10267069.aspx

namespace MessengerLibrary;

public class AsyncReaderWriterLock
{
    private readonly Queue<TaskCompletionSource<Releaser>> m_waitingWriters = new Queue<TaskCompletionSource<Releaser>>();
    private TaskCompletionSource<Releaser> m_waitingReader = new TaskCompletionSource<Releaser>();
    private int m_readersWaiting;
    private int m_status;
    private readonly Task<Releaser> m_readerReleaser;
    private readonly Task<Releaser> m_writerReleaser;


    public AsyncReaderWriterLock()
    {
        this.m_readerReleaser = Task.FromResult(new Releaser(this, false));
        this.m_writerReleaser = Task.FromResult(new Releaser(this, true));
    }


    public Task<Releaser> ReaderLockAsync()
    {
        lock (this.m_waitingWriters)
        {
            if (this.m_status >= 0 && this.m_waitingWriters.Count == 0)
            {
                ++this.m_status;
                return this.m_readerReleaser;
            }
            else
            {
                ++this.m_readersWaiting;
                return this.m_waitingReader.Task.ContinueWith(t => t.Result);
            }
        }
    }

    public Task<Releaser> WriterLockAsync()
    {
        lock (this.m_waitingWriters)
        {
            if (this.m_status == 0)
            {
                this.m_status = -1;
                return this.m_writerReleaser;
            }
            else
            {
                var waiter = new TaskCompletionSource<Releaser>();
                this.m_waitingWriters.Enqueue(waiter);
                return waiter.Task;
            }
        }
    }


    public void ReaderRelease()
    {
        TaskCompletionSource<Releaser> toWake = null;

        lock (this.m_waitingWriters)
        {
            --this.m_status;
            if (this.m_status == 0 && this.m_waitingWriters.Count > 0)
            {
                this.m_status = -1;
                toWake = this.m_waitingWriters.Dequeue();
            }
        }

        if (toWake != null)
            toWake.SetResult(new Releaser(this, true));
    }


    public void WriterRelease()
    {
        TaskCompletionSource<Releaser> toWake = null;
        bool toWakeIsWriter = false;

        lock (this.m_waitingWriters)
        {
            if (this.m_waitingWriters.Count > 0)
            {
                toWake = this.m_waitingWriters.Dequeue();
                toWakeIsWriter = true;
            }
            else if (this.m_readersWaiting > 0)
            {
                toWake = this.m_waitingReader;
                this.m_status = this.m_readersWaiting;
                this.m_readersWaiting = 0;
                this.m_waitingReader = new TaskCompletionSource<Releaser>();
            }
            else this.m_status = 0;
        }

        if (toWake != null)
            toWake.SetResult(new Releaser(this, toWakeIsWriter));
    }

    public struct Releaser : IDisposable
    {
        private readonly AsyncReaderWriterLock m_toRelease;
        private readonly bool m_writer;

        internal Releaser(AsyncReaderWriterLock toRelease, bool writer)
        {
            this.m_toRelease = toRelease;
            this.m_writer = writer;
        }

        public void Dispose()
        {
            if (this.m_toRelease != null)
            {
                if (this.m_writer) this.m_toRelease.WriterRelease();
                else this.m_toRelease.ReaderRelease();
            }
        }
    }

}