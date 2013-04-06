﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reactive.Subjects;

//credit for ReaderWriterAsync goes to Stephen Toub from Microsoft - thanks!
//http://blogs.msdn.com/b/pfxteam/archive/2012/02/12/10267069.aspx

namespace MessengerLibrary.Threading
{

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
            m_readerReleaser = Task.FromResult(new Releaser(this, false));
            m_writerReleaser = Task.FromResult(new Releaser(this, true));
        }


        public Task<Releaser> ReaderLockAsync()
        {
            lock (m_waitingWriters)
            {
                if (m_status >= 0 && m_waitingWriters.Count == 0)
                {
                    ++m_status;
                    return m_readerReleaser;
                }
                else
                {
                    ++m_readersWaiting;
                    return m_waitingReader.Task.ContinueWith(t => t.Result);
                }
            }
        }

        public Task<Releaser> WriterLockAsync()
        {
            lock (m_waitingWriters)
            {
                if (m_status == 0)
                {
                    m_status = -1;
                    return m_writerReleaser;
                }
                else
                {
                    var waiter = new TaskCompletionSource<Releaser>();
                    m_waitingWriters.Enqueue(waiter);
                    return waiter.Task;
                }
            }
        }


        public void ReaderRelease()
        {
            TaskCompletionSource<Releaser> toWake = null;

            lock (m_waitingWriters)
            {
                --m_status;
                if (m_status == 0 && m_waitingWriters.Count > 0)
                {
                    m_status = -1;
                    toWake = m_waitingWriters.Dequeue();
                }
            }

            if (toWake != null)
                toWake.SetResult(new Releaser(this, true));
        }


        public void WriterRelease()
        {
            TaskCompletionSource<Releaser> toWake = null;
            bool toWakeIsWriter = false;

            lock (m_waitingWriters)
            {
                if (m_waitingWriters.Count > 0)
                {
                    toWake = m_waitingWriters.Dequeue();
                    toWakeIsWriter = true;
                }
                else if (m_readersWaiting > 0)
                {
                    toWake = m_waitingReader;
                    m_status = m_readersWaiting;
                    m_readersWaiting = 0;
                    m_waitingReader = new TaskCompletionSource<Releaser>();
                }
                else m_status = 0;
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
                m_toRelease = toRelease;
                m_writer = writer;
            }

            public void Dispose()
            {
                if (m_toRelease != null)
                {
                    if (m_writer) m_toRelease.WriterRelease();
                    else m_toRelease.ReaderRelease();
                }
            }
        }

    }

}

