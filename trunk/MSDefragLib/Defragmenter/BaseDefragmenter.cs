﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MSDefragLib.Defragmenter
{
    public abstract class BaseDefragmenter : IDefragmenter
    {
        #region IDefragmenter Members

        private Thread defragThread;
        private Thread eventDispatcherThread;

        public event EventHandler<ProgressEventArgs> ProgressEvent
        {
            add { defragEventDispatcher.ProgressEvent += value; }
            remove { defragEventDispatcher.ProgressEvent -= value; }
        }
        //public event UpdateDiskMapEventHandler UpdateDiskMapEvent
        //{
        //    add { defragEventDispatcher.UpdateDiskMapEvent += value; }
        //    remove { defragEventDispatcher.UpdateDiskMapEvent -= value; }
        //}
        public event EventHandler<FilteredClusterEventArgs> UpdateFilteredDiskMapEvent
        {
            add { defragEventDispatcher.UpdateFilteredDiskMapEvent += value; }
            remove { defragEventDispatcher.UpdateFilteredDiskMapEvent -= value; }
        }

        public abstract void BeginDefragmentation(string parameter);
        public abstract void FinishDefragmentation(int timeoutMS);

        public abstract UInt64 NumClusters { get; set; }

        public abstract void ResendAllClusters();

        public abstract DefragEventDispatcher defragEventDispatcher { get; set; }

        //public abstract event LogMessageHandler LogMessage;

        public void StartDefragmentation(string parameter)
        {
            defragThread = new Thread(Defrag);
            defragThread.Priority = ThreadPriority.Lowest;

            defragThread.Start();

            eventDispatcherThread = new Thread(EventDispatcher);
            eventDispatcherThread.Priority = ThreadPriority.Normal;

            eventDispatcherThread.Start();
        }

        private void Defrag()
        {
            BeginDefragmentation(@"C:\*");
        }

        private void EventDispatcher()
        {
            defragEventDispatcher.StartEventDispatcher();
        }

        public void StopDefragmentation(int timeoutMs)
        {
            FinishDefragmentation(5000);

            if (defragThread.IsAlive)
            {
                defragThread.Interrupt();
                defragThread.Join();
            }

            if (eventDispatcherThread.IsAlive)
            {
                eventDispatcherThread.Interrupt();
                eventDispatcherThread.Join();
            }
        }

        #endregion
    }
}