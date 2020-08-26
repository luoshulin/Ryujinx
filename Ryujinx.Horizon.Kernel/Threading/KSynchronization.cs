using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Kernel.Common;
using System;
using System.Collections.Generic;

namespace Ryujinx.Horizon.Kernel.Threading
{
    class KSynchronization
    {
        private KernelContextInternal _context;

        public KSynchronization(KernelContextInternal context)
        {
            _context = context;
        }

        public Result WaitFor(Span<KSynchronizationObject> syncObjs, long timeout, out int handleIndex)
        {
            handleIndex = 0;

            Result result = KernelResult.TimedOut;

            _context.CriticalSection.Enter();

            // Check if objects are already signaled before waiting.
            for (int index = 0; index < syncObjs.Length; index++)
            {
                if (!syncObjs[index].IsSignaled())
                {
                    continue;
                }

                handleIndex = index;

                _context.CriticalSection.Leave();

                return Result.Success;
            }

            if (timeout == 0)
            {
                _context.CriticalSection.Leave();

                return result;
            }

            KThread currentThread = _context.Scheduler.GetCurrentThread();

            if (currentThread.ShallBeTerminated ||
                currentThread.SchedFlags == ThreadSchedState.TerminationPending)
            {
                result = KernelResult.ThreadTerminating;
            }
            else if (currentThread.SyncCancelled)
            {
                currentThread.SyncCancelled = false;

                result = KernelResult.Cancelled;
            }
            else
            {
                LinkedListNode<KThread>[] syncNodes = new LinkedListNode<KThread>[syncObjs.Length];

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    syncNodes[index] = syncObjs[index].AddWaitingThread(currentThread);
                }

                currentThread.WaitingSync = true;
                currentThread.SignaledObj = null;
                currentThread.ObjSyncResult = result;

                currentThread.Reschedule(ThreadSchedState.Paused);

                if (timeout > 0)
                {
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }

                _context.CriticalSection.Leave();

                currentThread.WaitingSync = false;

                if (timeout > 0)
                {
                    _context.TimeManager.UnscheduleFutureInvocation(currentThread);
                }

                _context.CriticalSection.Enter();

                result = currentThread.ObjSyncResult;

                handleIndex = -1;

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    syncObjs[index].RemoveWaitingThread(syncNodes[index]);

                    if (syncObjs[index] == currentThread.SignaledObj)
                    {
                        handleIndex = index;
                    }
                }
            }

            _context.CriticalSection.Leave();

            return result;
        }

        public void SignalObject(KSynchronizationObject syncObj)
        {
            _context.CriticalSection.Enter();

            if (syncObj.IsSignaled())
            {
                LinkedListNode<KThread> node = syncObj.WaitingThreads.First;

                while (node != null)
                {
                    KThread thread = node.Value;

                    if ((thread.SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Paused)
                    {
                        thread.SignaledObj = syncObj;
                        thread.ObjSyncResult = Result.Success;

                        thread.Reschedule(ThreadSchedState.Running);
                    }

                    node = node.Next;
                }
            }

            _context.CriticalSection.Leave();
        }
    }
}