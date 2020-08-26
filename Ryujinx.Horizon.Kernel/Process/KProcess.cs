using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Kernel.Common;
using Ryujinx.Horizon.Kernel.Memory;
using Ryujinx.Horizon.Kernel.Svc;
using Ryujinx.Horizon.Kernel.Threading;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Horizon.Kernel.Process
{
    class KProcess : KSynchronizationObject
    {
        public const int KernelVersionMajor = 10;
        public const int KernelVersionMinor = 4;
        public const int KernelVersionRevision = 0;

        public const int KernelVersionPacked =
            (KernelVersionMajor << 19) |
            (KernelVersionMinor << 15) |
            (KernelVersionRevision << 0);

        public KMemoryManager MemoryManager { get; private set; }

        private SortedDictionary<ulong, KTlsPageInfo> _fullTlsPages;
        private SortedDictionary<ulong, KTlsPageInfo> _freeTlsPages;

        public int DefaultCpuCore { get; set; }

        public bool Debug { get; private set; }

        public KResourceLimit ResourceLimit { get; private set; }

        public ulong PersonalMmHeapPagesCount { get; private set; }

        public KProcessState State { get; private set; }

        private object _processLock;
        private object _threadingLock;

        public KAddressArbiter AddressArbiter { get; private set; }

        public ulong[] RandomEntropy { get; private set; }

        private bool _signaled;

        public string Name { get; private set; }

        private int _threadCount;

        public ProcessCreationFlags Flags { get; private set; }

        public KMemoryRegion MemoryRegion { get; private set; }

        public bool AslrEnabled { get; private set; }

        public KProcessCapabilities Capabilities { get; private set; }

        public ulong TitleId { get; private set; }
        public long Pid { get; private set; }

        private long _creationTimestamp;
        private ulong _entrypoint;
        private ThreadStart _customThreadStart;
        private ulong _imageSize;
        private ulong _mainThreadStackSize;
        private ulong _memoryUsageCapacity;
        private int _version;

        public KHandleTable HandleTable { get; private set; }

        public ulong UserExceptionContextAddress { get; private set; }

        private LinkedList<KThread> _threads;

        public bool IsPaused { get; private set; }

        private IProcessContextFactory _contextFactory;
        public IProcessContext Context { get; private set; }
        public IAddressSpaceManager CpuMemory => Context.AddressSpace;

        public HleProcessDebugger Debugger { get; private set; }

        public KProcess(KernelContextInternal context) : base(context)
        {
            _processLock = new object();
            _threadingLock = new object();

            AddressArbiter = new KAddressArbiter(context);

            _fullTlsPages = new SortedDictionary<ulong, KTlsPageInfo>();
            _freeTlsPages = new SortedDictionary<ulong, KTlsPageInfo>();

            Capabilities = new KProcessCapabilities();

            RandomEntropy = new ulong[KScheduler.CpuCoresCount];

            // TODO: Remove once we no longer need to initialize it externally.
            HandleTable = new KHandleTable(context);

            _threads = new LinkedList<KThread>();

            Debugger = new HleProcessDebugger(this);
        }

        public Result InitializeKip(
            ProcessCreationInfo creationInfo,
            ReadOnlySpan<int> capabilities,
            KPageList pageList,
            KResourceLimit resourceLimit,
            KMemoryRegion memoryRegion,
            IProcessContextFactory contextFactory)
        {
            ResourceLimit = resourceLimit;
            MemoryRegion = memoryRegion;
            _contextFactory = contextFactory ?? new ProcessContextFactory();

            AddressSpaceType addrSpaceType = (AddressSpaceType)((int)(creationInfo.Flags & ProcessCreationFlags.AddressSpaceMask) >> (int)ProcessCreationFlags.AddressSpaceShift);

            InitializeMemoryManager(creationInfo.Flags);

            bool aslrEnabled = creationInfo.Flags.HasFlag(ProcessCreationFlags.EnableAslr);

            AslrEnabled = aslrEnabled;

            ulong codeAddress = creationInfo.CodeAddress;

            ulong codeSize = (ulong)creationInfo.CodePagesCount * KMemoryManager.PageSize;

            KMemoryBlockAllocator memoryBlockAllocator = creationInfo.Flags.HasFlag(ProcessCreationFlags.IsApplication)
                ? KernelContext.LargeMemoryBlockAllocator
                : KernelContext.SmallMemoryBlockAllocator;

            Result result = MemoryManager.InitializeForProcess(
                addrSpaceType,
                aslrEnabled,
                !aslrEnabled,
                memoryRegion,
                codeAddress,
                codeSize,
                memoryBlockAllocator);

            if (result != Result.Success)
            {
                return result;
            }

            if (!MemoryManager.CanContain(codeAddress, codeSize, KMemoryState.CodeStatic))
            {
                return KernelResult.InvalidMemRange;
            }

            result = MemoryManager.MapPages(
                codeAddress,
                pageList,
                KMemoryState.CodeStatic,
                KMemoryPermission.None);

            if (result != Result.Success)
            {
                return result;
            }

            result = Capabilities.InitializeForKernel(capabilities, MemoryManager);

            if (result != Result.Success)
            {
                return result;
            }

            Pid = KernelContext.NewKipId();

            if (Pid == 0 || (ulong)Pid >= KernelConstants.InitialProcessId)
            {
                throw new InvalidOperationException($"Invalid KIP Id {Pid}.");
            }

            result = ParseProcessInfo(creationInfo);

            return result;
        }

        public Result Initialize(
            ProcessCreationInfo creationInfo,
            ReadOnlySpan<int> capabilities,
            KResourceLimit resourceLimit,
            KMemoryRegion memoryRegion,
            IProcessContextFactory contextFactory,
            ThreadStart customThreadStart = null)
        {
            ResourceLimit = resourceLimit;
            MemoryRegion = memoryRegion;
            _contextFactory = contextFactory ?? new ProcessContextFactory();

            ulong personalMmHeapSize = GetPersonalMmHeapSize((ulong)creationInfo.SystemResourcePagesCount, memoryRegion);

            ulong codePagesCount = (ulong)creationInfo.CodePagesCount;

            ulong neededSizeForProcess = personalMmHeapSize + codePagesCount * KMemoryManager.PageSize;

            if (neededSizeForProcess != 0 && resourceLimit != null)
            {
                if (!resourceLimit.Reserve(LimitableResource.Memory, neededSizeForProcess))
                {
                    return KernelResult.ResLimitExceeded;
                }
            }

            void CleanUpForError()
            {
                if (neededSizeForProcess != 0 && resourceLimit != null)
                {
                    resourceLimit.Release(LimitableResource.Memory, neededSizeForProcess);
                }
            }

            PersonalMmHeapPagesCount = (ulong)creationInfo.SystemResourcePagesCount;

            KMemoryBlockAllocator memoryBlockAllocator;

            if (PersonalMmHeapPagesCount != 0)
            {
                memoryBlockAllocator = new KMemoryBlockAllocator(PersonalMmHeapPagesCount * KMemoryManager.PageSize);
            }
            else
            {
                memoryBlockAllocator = creationInfo.Flags.HasFlag(ProcessCreationFlags.IsApplication)
                    ? KernelContext.LargeMemoryBlockAllocator
                    : KernelContext.SmallMemoryBlockAllocator;
            }

            AddressSpaceType addrSpaceType = (AddressSpaceType)((int)(creationInfo.Flags & ProcessCreationFlags.AddressSpaceMask) >> (int)ProcessCreationFlags.AddressSpaceShift);

            InitializeMemoryManager(creationInfo.Flags);

            bool aslrEnabled = creationInfo.Flags.HasFlag(ProcessCreationFlags.EnableAslr);

            AslrEnabled = aslrEnabled;

            ulong codeAddress = creationInfo.CodeAddress;

            ulong codeSize = codePagesCount * KMemoryManager.PageSize;

            Result result = MemoryManager.InitializeForProcess(
                addrSpaceType,
                aslrEnabled,
                !aslrEnabled,
                memoryRegion,
                codeAddress,
                codeSize,
                memoryBlockAllocator);

            if (result != Result.Success)
            {
                CleanUpForError();

                return result;
            }

            if (!MemoryManager.CanContain(codeAddress, codeSize, KMemoryState.CodeStatic))
            {
                CleanUpForError();

                return KernelResult.InvalidMemRange;
            }

            result = MemoryManager.MapNewProcessCode(
                codeAddress,
                codePagesCount,
                KMemoryState.CodeStatic,
                KMemoryPermission.None);

            if (result != Result.Success)
            {
                CleanUpForError();

                return result;
            }

            result = Capabilities.InitializeForUser(capabilities, MemoryManager);

            if (result != Result.Success)
            {
                CleanUpForError();

                return result;
            }

            Pid = KernelContext.NewProcessId();

            if (Pid == -1 || (ulong)Pid < KernelConstants.InitialProcessId)
            {
                throw new InvalidOperationException($"Invalid Process Id {Pid}.");
            }

            result = ParseProcessInfo(creationInfo);

            if (result != Result.Success)
            {
                CleanUpForError();
            }

            _customThreadStart = customThreadStart;

            return result;
        }

        private Result ParseProcessInfo(ProcessCreationInfo creationInfo)
        {
            // Ensure that the current kernel version is equal or above to the minimum required.
            uint requiredKernelVersionMajor = (uint)Capabilities.KernelReleaseVersion >> 19;
            uint requiredKernelVersionMinor = ((uint)Capabilities.KernelReleaseVersion >> 15) & 0xf;

            if (KernelContext.EnableVersionChecks)
            {
                if (requiredKernelVersionMajor > KernelVersionMajor)
                {
                    return KernelResult.InvalidCombination;
                }

                if (requiredKernelVersionMajor != KernelVersionMajor && requiredKernelVersionMajor < 3)
                {
                    return KernelResult.InvalidCombination;
                }

                if (requiredKernelVersionMinor > KernelVersionMinor)
                {
                    return KernelResult.InvalidCombination;
                }
            }

            Result result = AllocateThreadLocalStorage(out ulong userExceptionContextAddress);

            if (result != Result.Success)
            {
                return result;
            }

            UserExceptionContextAddress = userExceptionContextAddress;

            CpuMemory.Fill(userExceptionContextAddress, KTlsPageInfo.TlsEntrySize, 0);

            Name = creationInfo.Name;

            State = KProcessState.Created;

            _creationTimestamp = PerformanceCounter.ElapsedMilliseconds;

            Flags = creationInfo.Flags;
            _version = creationInfo.Version;
            TitleId = creationInfo.TitleId;
            _entrypoint = creationInfo.CodeAddress;
            _imageSize = (ulong)creationInfo.CodePagesCount * KMemoryManager.PageSize;

            switch (Flags & ProcessCreationFlags.AddressSpaceMask)
            {
                case ProcessCreationFlags.AddressSpace32Bit:
                case ProcessCreationFlags.AddressSpace64BitDeprecated:
                case ProcessCreationFlags.AddressSpace64Bit:
                    _memoryUsageCapacity = MemoryManager.HeapRegionEnd -
                                           MemoryManager.HeapRegionStart;
                    break;

                case ProcessCreationFlags.AddressSpace32BitWithoutAlias:
                    _memoryUsageCapacity = MemoryManager.HeapRegionEnd -
                                           MemoryManager.HeapRegionStart +
                                           MemoryManager.AliasRegionEnd -
                                           MemoryManager.AliasRegionStart;
                    break;

                default: throw new InvalidOperationException($"Invalid MMU flags value 0x{Flags:x2}.");
            }

            GenerateRandomEntropy();

            return Result.Success;
        }

        public Result AllocateThreadLocalStorage(out ulong address)
        {
            KernelContext.CriticalSection.Enter();

            Result result;

            if (_freeTlsPages.Count > 0)
            {
                // If we have free TLS pages available, just use the first one.
                KTlsPageInfo pageInfo = _freeTlsPages.Values.First();

                if (!pageInfo.TryGetFreePage(out address))
                {
                    throw new InvalidOperationException("Unexpected failure getting free TLS page!");
                }

                if (pageInfo.IsFull())
                {
                    _freeTlsPages.Remove(pageInfo.PageAddr);

                    _fullTlsPages.Add(pageInfo.PageAddr, pageInfo);
                }

                result = Result.Success;
            }
            else
            {
                // Otherwise, we need to create a new one.
                result = AllocateTlsPage(out KTlsPageInfo pageInfo);

                if (result == Result.Success)
                {
                    if (!pageInfo.TryGetFreePage(out address))
                    {
                        throw new InvalidOperationException("Unexpected failure getting free TLS page!");
                    }

                    _freeTlsPages.Add(pageInfo.PageAddr, pageInfo);
                }
                else
                {
                    address = 0;
                }
            }

            KernelContext.CriticalSection.Leave();

            return result;
        }

        private Result AllocateTlsPage(out KTlsPageInfo pageInfo)
        {
            pageInfo = default;

            if (!KernelContext.UserSlabHeapPages.TryGetItem(out ulong tlsPagePa))
            {
                return KernelResult.OutOfMemory;
            }

            ulong regionStart = MemoryManager.TlsIoRegionStart;
            ulong regionSize = MemoryManager.TlsIoRegionEnd - regionStart;

            ulong regionPagesCount = regionSize / KMemoryManager.PageSize;

            Result result = MemoryManager.AllocateOrMapPa(
                1,
                KMemoryManager.PageSize,
                tlsPagePa,
                true,
                regionStart,
                regionPagesCount,
                KMemoryState.ThreadLocal,
                KMemoryPermission.ReadAndWrite,
                out ulong tlsPageVa);

            if (result != Result.Success)
            {
                KernelContext.UserSlabHeapPages.Free(tlsPagePa);
            }
            else
            {
                pageInfo = new KTlsPageInfo(tlsPageVa);

                CpuMemory.Fill(tlsPageVa, KMemoryManager.PageSize, 0);
            }

            return result;
        }

        public Result FreeThreadLocalStorage(ulong tlsSlotAddr)
        {
            ulong tlsPageAddr = BitUtils.AlignDown(tlsSlotAddr, KMemoryManager.PageSize);

            KernelContext.CriticalSection.Enter();

            Result result = Result.Success;


            if (_fullTlsPages.TryGetValue(tlsPageAddr, out KTlsPageInfo pageInfo))
            {
                // TLS page was full, free slot and move to free pages tree.
                _fullTlsPages.Remove(tlsPageAddr);

                _freeTlsPages.Add(tlsPageAddr, pageInfo);
            }
            else if (!_freeTlsPages.TryGetValue(tlsPageAddr, out pageInfo))
            {
                result = KernelResult.InvalidAddress;
            }

            if (pageInfo != null)
            {
                pageInfo.FreeTlsSlot(tlsSlotAddr);

                if (pageInfo.IsEmpty())
                {
                    // TLS page is now empty, we should ensure it is removed
                    // from all trees, and free the memory it was using.
                    _freeTlsPages.Remove(tlsPageAddr);

                    KernelContext.CriticalSection.Leave();

                    FreeTlsPage(pageInfo);

                    return Result.Success;
                }
            }

            KernelContext.CriticalSection.Leave();

            return result;
        }

        private Result FreeTlsPage(KTlsPageInfo pageInfo)
        {
            if (!MemoryManager.TryConvertVaToPa(pageInfo.PageAddr, out ulong tlsPagePa))
            {
                throw new InvalidOperationException("Unexpected failure translating virtual address to physical.");
            }

            Result result = MemoryManager.UnmapForKernel(pageInfo.PageAddr, 1, KMemoryState.ThreadLocal);

            if (result == Result.Success)
            {
                KernelContext.UserSlabHeapPages.Free(tlsPagePa);
            }

            return result;
        }

        private void GenerateRandomEntropy()
        {
            // TODO.
        }

        public Result Start(int mainThreadPriority, ulong stackSize)
        {
            lock (_processLock)
            {
                if (State > KProcessState.CreatedAttached)
                {
                    return KernelResult.InvalidState;
                }

                if (ResourceLimit != null && !ResourceLimit.Reserve(LimitableResource.Thread, 1))
                {
                    return KernelResult.ResLimitExceeded;
                }

                KResourceLimit threadResourceLimit = ResourceLimit;
                KResourceLimit memoryResourceLimit = null;

                if (_mainThreadStackSize != 0)
                {
                    throw new InvalidOperationException("Trying to start a process with a invalid state!");
                }

                ulong stackSizeRounded = BitUtils.AlignUp(stackSize, KMemoryManager.PageSize);

                ulong neededSize = stackSizeRounded + _imageSize;

                // Check if the needed size for the code and the stack will fit on the
                // memory usage capacity of this Process. Also check for possible overflow
                // on the above addition.
                if (neededSize > _memoryUsageCapacity || neededSize < stackSizeRounded)
                {
                    threadResourceLimit?.Release(LimitableResource.Thread, 1);

                    return KernelResult.OutOfMemory;
                }

                if (stackSizeRounded != 0 && ResourceLimit != null)
                {
                    memoryResourceLimit = ResourceLimit;

                    if (!memoryResourceLimit.Reserve(LimitableResource.Memory, stackSizeRounded))
                    {
                        threadResourceLimit?.Release(LimitableResource.Thread, 1);

                        return KernelResult.ResLimitExceeded;
                    }
                }

                Result result;

                KThread mainThread = null;

                ulong stackTop = 0;

                void CleanUpForError()
                {
                    HandleTable.Destroy();

                    mainThread?.DecrementReferenceCount();

                    if (_mainThreadStackSize != 0)
                    {
                        ulong stackBottom = stackTop - _mainThreadStackSize;

                        ulong stackPagesCount = _mainThreadStackSize / KMemoryManager.PageSize;

                        MemoryManager.UnmapForKernel(stackBottom, stackPagesCount, KMemoryState.Stack);

                        _mainThreadStackSize = 0;
                    }

                    memoryResourceLimit?.Release(LimitableResource.Memory, stackSizeRounded);
                    threadResourceLimit?.Release(LimitableResource.Thread, 1);
                }

                if (stackSizeRounded != 0)
                {
                    ulong stackPagesCount = stackSizeRounded / KMemoryManager.PageSize;

                    ulong regionStart = MemoryManager.StackRegionStart;
                    ulong regionSize = MemoryManager.StackRegionEnd - regionStart;

                    ulong regionPagesCount = regionSize / KMemoryManager.PageSize;

                    result = MemoryManager.AllocateOrMapPa(
                        stackPagesCount,
                        KMemoryManager.PageSize,
                        0,
                        false,
                        regionStart,
                        regionPagesCount,
                        KMemoryState.Stack,
                        KMemoryPermission.ReadAndWrite,
                        out ulong stackBottom);

                    if (result != Result.Success)
                    {
                        CleanUpForError();

                        return result;
                    }

                    _mainThreadStackSize += stackSizeRounded;

                    stackTop = stackBottom + stackSizeRounded;
                }

                ulong heapCapacity = _memoryUsageCapacity - _mainThreadStackSize - _imageSize;

                result = MemoryManager.SetHeapCapacity(heapCapacity);

                if (result != Result.Success)
                {
                    CleanUpForError();

                    return result;
                }

                HandleTable = new KHandleTable(KernelContext);

                result = HandleTable.Initialize(Capabilities.HandleTableSize);

                if (result != Result.Success)
                {
                    CleanUpForError();

                    return result;
                }

                mainThread = new KThread(KernelContext);

                result = mainThread.Initialize(
                    _entrypoint,
                    0,
                    stackTop,
                    mainThreadPriority,
                    DefaultCpuCore,
                    this,
                    ThreadType.User,
                    _customThreadStart);

                if (result != Result.Success)
                {
                    CleanUpForError();

                    return result;
                }

                result = HandleTable.GenerateHandle(mainThread, out int mainThreadHandle);

                if (result != Result.Success)
                {
                    CleanUpForError();

                    return result;
                }

                mainThread.SetEntryArguments(0, mainThreadHandle);

                KProcessState oldState = State;
                KProcessState newState = State != KProcessState.Created
                    ? KProcessState.Attached
                    : KProcessState.Started;

                SetState(newState);

                result = mainThread.Start();

                if (result != Result.Success)
                {
                    SetState(oldState);

                    CleanUpForError();
                }

                if (result == Result.Success)
                {
                    mainThread.IncrementReferenceCount();
                }

                mainThread.DecrementReferenceCount();

                return result;
            }
        }

        private void SetState(KProcessState newState)
        {
            if (State != newState)
            {
                State = newState;
                _signaled = true;

                Signal();
            }
        }

        public Result InitializeThread(
            KThread thread,
            ulong entrypoint,
            ulong argsPtr,
            ulong stackTop,
            int priority,
            int cpuCore)
        {
            lock (_processLock)
            {
                return thread.Initialize(entrypoint, argsPtr, stackTop, priority, cpuCore, this, ThreadType.User, null);
            }
        }

        public void IncrementThreadCount()
        {
            Interlocked.Increment(ref _threadCount);
        }

        public void DecrementThreadCountAndTerminateIfZero()
        {
            if (Interlocked.Decrement(ref _threadCount) == 0)
            {
                Terminate();
            }
        }

        public void DecrementToZeroWhileTerminatingCurrent()
        {
            while (Interlocked.Decrement(ref _threadCount) != 0)
            {
                Destroy();
                TerminateCurrentProcess();
            }

            // Nintendo panic here because if it reaches this point, the current thread should be already dead.
            // As we handle the death of the thread in the post SVC handler and inside the CPU emulator, we don't panic here.
        }

        public ulong GetMemoryCapacity()
        {
            ulong totalCapacity = (ulong)ResourceLimit.GetRemainingValue(LimitableResource.Memory);

            totalCapacity += MemoryManager.GetTotalHeapSize();

            totalCapacity += GetPersonalMmHeapSize();

            totalCapacity += _imageSize + _mainThreadStackSize;

            if (totalCapacity <= _memoryUsageCapacity)
            {
                return totalCapacity;
            }

            return _memoryUsageCapacity;
        }

        public ulong GetMemoryUsage()
        {
            return _imageSize + _mainThreadStackSize + MemoryManager.GetTotalHeapSize() + GetPersonalMmHeapSize();
        }

        public ulong GetMemoryCapacityWithoutPersonalMmHeap()
        {
            return GetMemoryCapacity() - GetPersonalMmHeapSize();
        }

        public ulong GetMemoryUsageWithoutPersonalMmHeap()
        {
            return GetMemoryUsage() - GetPersonalMmHeapSize();
        }

        private ulong GetPersonalMmHeapSize()
        {
            return GetPersonalMmHeapSize(PersonalMmHeapPagesCount, MemoryRegion);
        }

        private static ulong GetPersonalMmHeapSize(ulong personalMmHeapPagesCount, KMemoryRegion memRegion)
        {
            if (memRegion == KMemoryRegion.Applet)
            {
                return 0;
            }

            return personalMmHeapPagesCount * KMemoryManager.PageSize;
        }

        public void AddThread(KThread thread)
        {
            lock (_threadingLock)
            {
                thread.ProcessListNode = _threads.AddLast(thread);
            }
        }

        public void RemoveThread(KThread thread)
        {
            lock (_threadingLock)
            {
                _threads.Remove(thread.ProcessListNode);
            }
        }

        public bool IsCpuCoreAllowed(int core)
        {
            return (Capabilities.AllowedCpuCoresMask & (1L << core)) != 0;
        }

        public bool IsPriorityAllowed(int priority)
        {
            return (Capabilities.AllowedThreadPriosMask & (1L << priority)) != 0;
        }

        public override bool IsSignaled()
        {
            return _signaled;
        }

        public Result Terminate()
        {
            Result result;

            bool shallTerminate = false;

            KernelContext.CriticalSection.Enter();

            lock (_processLock)
            {
                if (State >= KProcessState.Started)
                {
                    if (State == KProcessState.Started ||
                        State == KProcessState.Crashed ||
                        State == KProcessState.Attached ||
                        State == KProcessState.DebugSuspended)
                    {
                        SetState(KProcessState.Exiting);

                        shallTerminate = true;
                    }

                    result = Result.Success;
                }
                else
                {
                    result = KernelResult.InvalidState;
                }
            }

            KernelContext.CriticalSection.Leave();

            if (shallTerminate)
            {
                UnpauseAndTerminateAllThreadsExcept(KernelContext.Scheduler.GetCurrentThread());

                HandleTable.Destroy();

                SignalExitToDebugTerminated();
                SignalExit();
            }

            return result;
        }

        public void TerminateCurrentProcess()
        {
            bool shallTerminate = false;

            KernelContext.CriticalSection.Enter();

            lock (_processLock)
            {
                if (State >= KProcessState.Started)
                {
                    if (State == KProcessState.Started ||
                        State == KProcessState.Attached ||
                        State == KProcessState.DebugSuspended)
                    {
                        SetState(KProcessState.Exiting);

                        shallTerminate = true;
                    }
                }
            }

            KernelContext.CriticalSection.Leave();

            if (shallTerminate)
            {
                UnpauseAndTerminateAllThreadsExcept(KernelContext.Scheduler.GetCurrentThread());

                HandleTable.Destroy();

                // NOTE: this is supposed to be called in receiving of the mailbox.
                SignalExitToDebugExited();
                SignalExit();
            }
        }

        private void UnpauseAndTerminateAllThreadsExcept(KThread currentThread)
        {
            lock (_threadingLock)
            {
                KernelContext.CriticalSection.Enter();

                foreach (KThread thread in _threads)
                {
                    if ((thread.SchedFlags & ThreadSchedState.LowMask) != ThreadSchedState.TerminationPending)
                    {
                        thread.PrepareForTermination();
                    }
                }

                KernelContext.CriticalSection.Leave();
            }

            while (true)
            {
                KThread blockedThread = null;

                lock (_threadingLock)
                {
                    foreach (KThread thread in _threads)
                    {
                        if (thread != currentThread && (thread.SchedFlags & ThreadSchedState.LowMask) != ThreadSchedState.TerminationPending)
                        {
                            thread.IncrementReferenceCount();

                            blockedThread = thread;
                            break;
                        }
                    }
                }

                if (blockedThread == null)
                {
                    break;
                }

                blockedThread.Terminate();
                blockedThread.DecrementReferenceCount();
            }
        }

        private void SignalExitToDebugTerminated()
        {
            // TODO: Debug events.
        }

        private void SignalExitToDebugExited()
        {
            // TODO: Debug events.
        }

        private void SignalExit()
        {
            if (ResourceLimit != null)
            {
                ResourceLimit.Release(LimitableResource.Memory, GetMemoryUsage());
            }

            KernelContext.CriticalSection.Enter();

            SetState(KProcessState.Exited);

            KernelContext.CriticalSection.Leave();
        }

        public Result ClearIfNotExited()
        {
            Result result;

            KernelContext.CriticalSection.Enter();

            lock (_processLock)
            {
                if (State != KProcessState.Exited && _signaled)
                {
                    _signaled = false;

                    result = Result.Success;
                }
                else
                {
                    result = KernelResult.InvalidState;
                }
            }

            KernelContext.CriticalSection.Leave();

            return result;
        }

        private void InitializeMemoryManager(ProcessCreationFlags flags)
        {
            int addrSpaceBits = (flags & ProcessCreationFlags.AddressSpaceMask) switch
            {
                ProcessCreationFlags.AddressSpace32Bit => 32,
                ProcessCreationFlags.AddressSpace64BitDeprecated => 36,
                ProcessCreationFlags.AddressSpace32BitWithoutAlias => 32,
                ProcessCreationFlags.AddressSpace64Bit => 39,
                _ => 39
            };

            Context = _contextFactory.Create(KernelContext.Memory, 1UL << addrSpaceBits);

            MemoryManager = new KMemoryManager(KernelContext, CpuMemory);
        }

        protected override void Destroy()
        {
            Context.Dispose();
        }
    }
}