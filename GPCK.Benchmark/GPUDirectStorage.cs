using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using TerraFX.Interop.Windows;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace GPCK.Benchmark
{
    internal static class DStorageConstants
    {
        public const uint DSTORAGE_REQUEST_SOURCE_MEMORY = 1;
        public const uint DSTORAGE_REQUEST_DESTINATION_MEMORY = 0;
        public const uint DSTORAGE_REQUEST_DESTINATION_BUFFER = 1;
        public const int DSTORAGE_PRIORITY_NORMAL = 0;
        public const uint DSTORAGE_COMPRESSION_FORMAT_GDEFLATE = 1;
        public const ushort DSTORAGE_MAX_QUEUE_CAPACITY = 0x2000;
        public const ushort DSTORAGE_MIN_QUEUE_CAPACITY = 128;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DSTORAGE_CONFIGURATION1
    {
        public uint NumSubmitThreads;
        public uint NumBuiltInCpuDecompressionThreads;
        public int ForceLegacyMapping;
        public int DisableBypassIO;
        public int DisableTelemetry;
        public int DisableGpuDecompressionMetacommand;
        public int DisableGpuDecompressionSystemMemoryFallback;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct DSTORAGE_QUEUE_DESC
    {
        [FieldOffset(0)] public uint SourceType;
        [FieldOffset(4)] public ushort Capacity;
        [FieldOffset(8)] public int Priority;
        [FieldOffset(16)] public IntPtr Name;
        [FieldOffset(24)] public IntPtr Device;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct DSTORAGE_REQUEST
    {
        [FieldOffset(0)] public ulong Options;
        [FieldOffset(8)] public DSTORAGE_SOURCE Source;
        [FieldOffset(32)] public DSTORAGE_DESTINATION Destination;
        [FieldOffset(72)] public uint UncompressedSize;
        [FieldOffset(80)] public ulong CancellationTag;
        [FieldOffset(88)] public byte* Name;

        public void SetOptions(uint sourceType, uint destType, uint compressionFormat)
        {
            ulong src = (ulong)sourceType & 0x1UL;
            ulong dst = ((ulong)destType & 0x7FUL) << 1;
            ulong fmt = ((ulong)compressionFormat & 0xFFUL) << 8;
            Options = src | dst | fmt;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct DSTORAGE_SOURCE
    {
        [FieldOffset(0)] public DSTORAGE_SOURCE_MEMORY Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DSTORAGE_SOURCE_MEMORY
    {
        public void* Source;
        public uint Size;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct DSTORAGE_DESTINATION
    {
        [FieldOffset(0)] public DSTORAGE_DESTINATION_MEMORY Memory;
        [FieldOffset(0)] public DSTORAGE_DESTINATION_BUFFER Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DSTORAGE_DESTINATION_MEMORY
    {
        public void* Buffer;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DSTORAGE_DESTINATION_BUFFER
    {
        public ID3D12Resource* Resource;
        public ulong Offset;
        public uint Size;
    }

    [Guid("6924ea0c-c3cd-4826-b10a-f64f4ed927c1")]
    internal unsafe struct IDStorageFactory
    {
        #pragma warning disable 0649
        public void** lpVtbl;
        #pragma warning restore 0649

        public int CreateQueue(DSTORAGE_QUEUE_DESC* desc, Guid* riid, void** ppv)
        {
            return ((delegate* unmanaged[Stdcall]<IDStorageFactory*, DSTORAGE_QUEUE_DESC*, Guid*, void**, int>)lpVtbl[3])((IDStorageFactory*)Unsafe.AsPointer(ref this), desc, riid, ppv);
        }

        public int SetStagingBufferSize(uint size)
        {
             return ((delegate* unmanaged[Stdcall]<IDStorageFactory*, uint, int>)lpVtbl[6])((IDStorageFactory*)Unsafe.AsPointer(ref this), size);
        }

        public int SetDebugFlags(uint flags)
        {
             return ((delegate* unmanaged[Stdcall]<IDStorageFactory*, uint, int>)lpVtbl[7])((IDStorageFactory*)Unsafe.AsPointer(ref this), flags);
        }

        public uint Release()
        {
            return ((delegate* unmanaged[Stdcall]<IDStorageFactory*, uint>)lpVtbl[2])((IDStorageFactory*)Unsafe.AsPointer(ref this));
        }
    }

    [Guid("cfdbd83f-9e06-4fda-83ef-647f42b59529")]
    internal unsafe struct IDStorageQueue
    {
        #pragma warning disable 0649
        public void** lpVtbl;
        #pragma warning restore 0649

        public void EnqueueRequest(DSTORAGE_REQUEST* request)
        {
             ((delegate* unmanaged[Stdcall]<IDStorageQueue*, DSTORAGE_REQUEST*, void>)lpVtbl[3])((IDStorageQueue*)Unsafe.AsPointer(ref this), request);
        }

        public void EnqueueSignal(ID3D12Fence* fence, ulong value)
        {
             ((delegate* unmanaged[Stdcall]<IDStorageQueue*, ID3D12Fence*, ulong, void>)lpVtbl[4])((IDStorageQueue*)Unsafe.AsPointer(ref this), fence, value);
        }

        public void Submit()
        {
             ((delegate* unmanaged[Stdcall]<IDStorageQueue*, void>)lpVtbl[5])((IDStorageQueue*)Unsafe.AsPointer(ref this));
        }

        public uint Release()
        {
            return ((delegate* unmanaged[Stdcall]<IDStorageQueue*, uint>)lpVtbl[2])((IDStorageQueue*)Unsafe.AsPointer(ref this));
        }
    }

    public unsafe class GpuDirectStorage : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private delegate int DStorageGetFactoryDelegate(Guid* riid, void** ppv);
        private delegate int DStorageSetConfiguration1Delegate(DSTORAGE_CONFIGURATION1* config);

        private ComPtr<ID3D12Device> _device;
        private IDStorageFactory* _factory = null;
        private IDStorageQueue* _queue = null;
        private ComPtr<ID3D12Fence> _fence;
        private ulong _fenceValue = 0;
        private HANDLE _fenceEvent;

        private IntPtr _hDStorage;

        public bool IsSupported { get; private set; } = false;
        public bool IsHardwareAccelerated { get; private set; } = false;
        public string InitError { get; private set; } = "";

        public GpuDirectStorage()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string dllPath = Path.Combine(baseDir, "dstorage.dll");
                string corePath = Path.Combine(baseDir, "dstoragecore.dll");

                if (!File.Exists(dllPath)) { InitError = "dstorage.dll missing"; return; }
                if (!File.Exists(corePath)) { InitError = "dstoragecore.dll missing"; return; }

                // 1. Setup DLL Directory
                SetDllDirectoryW(baseDir);

                // 2. Load dstorage.dll
                _hDStorage = LoadLibraryW(dllPath);

                if (_hDStorage == IntPtr.Zero)
                {
                    InitError = $"Failed to load dstorage.dll (Win32 Error: {Marshal.GetLastWin32Error()})";
                    return;
                }

                // 3. Configure DirectStorage
                IntPtr pSetConfig = GetProcAddress(_hDStorage, "DStorageSetConfiguration1");
                if (pSetConfig != IntPtr.Zero)
                {
                    var setConfig = Marshal.GetDelegateForFunctionPointer<DStorageSetConfiguration1Delegate>(pSetConfig);
                    DSTORAGE_CONFIGURATION1 config = new DSTORAGE_CONFIGURATION1();

                    config.NumSubmitThreads = 0;
                    config.NumBuiltInCpuDecompressionThreads = 0;
                    config.ForceLegacyMapping = 1; // REQUIRED for compatibility
                    config.DisableBypassIO = 1;    // REQUIRED to fix Core Init Failed (0x89240003)
                    config.DisableTelemetry = 1;
                    config.DisableGpuDecompressionMetacommand = 1; // Fallback to shader to avoid driver issues
                    config.DisableGpuDecompressionSystemMemoryFallback = 0;

                    int hrConfig = setConfig(&config);
                    // Ignore config error, as it might just warn on some systems
                }

                // 4. Get Factory
                IntPtr pGetFactory = GetProcAddress(_hDStorage, "DStorageGetFactory");
                if (pGetFactory == IntPtr.Zero) { InitError = "DStorageGetFactory export not found"; return; }
                var getFactory = Marshal.GetDelegateForFunctionPointer<DStorageGetFactoryDelegate>(pGetFactory);

                // 5. Init Factory
                Guid uuidFactory = new Guid("6924ea0c-c3cd-4826-b10a-f64f4ed927c1");
                int hrFactory;
                fixed(IDStorageFactory** ppFactory = &_factory)
                {
                    hrFactory = getFactory(&uuidFactory, (void**)ppFactory);
                }

                if (FAILED(hrFactory))
                {
                    InitError = $"Failed to get DStorage Factory (0x{hrFactory:X})";
                    return;
                }

                // 6. Debug Flags & Staging
                _factory->SetDebugFlags(0x1); // Show Errors

                // CRITICAL FIX: Set Staging Buffer Size.
                // For DSTORAGE_REQUEST_SOURCE_MEMORY, a staging buffer is mandatory for CPU->GPU upload.
                // If not set, it defaults to 0 or 32MB depending on version, but explicit setting fixes initialization errors.
                _factory->SetStagingBufferSize(128 * 1024 * 1024); // 128MB

                // 7. Create D3D12 Device
                Guid uuidDevice = typeof(ID3D12Device).GUID;
                int hrDevice = D3D12CreateDevice(null, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_0, &uuidDevice, (void**)_device.GetAddressOf());
                bool deviceReady = SUCCEEDED(hrDevice);

                // 8. Create Queue
                Guid uuidQueue = new Guid("cfdbd83f-9e06-4fda-83ef-647f42b59529");
                int hrQueue = -1;

                if (deviceReady)
                {
                    if (TryCreateQueue(DStorageConstants.DSTORAGE_MAX_QUEUE_CAPACITY, (IntPtr)_device.Get(), uuidQueue, out hrQueue))
                    {
                        IsHardwareAccelerated = true;
                        goto QueueCreated;
                    }
                }

                IntPtr devicePtr = deviceReady ? (IntPtr)_device.Get() : IntPtr.Zero;
                if (TryCreateQueue(DStorageConstants.DSTORAGE_MIN_QUEUE_CAPACITY, devicePtr, uuidQueue, out hrQueue))
                {
                    IsHardwareAccelerated = false;
                    goto QueueCreated;
                }

                InitError = $"Failed to create DStorage Queue (HR: 0x{hrQueue:X}). Core init failed.";
                return;

            QueueCreated:

                // 9. Create Fence
                Guid uuidFence = typeof(ID3D12Fence).GUID;
                int hrFence;

                if (deviceReady)
                {
                     hrFence = _device.Get()->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, &uuidFence, (void**)_fence.GetAddressOf());
                }
                else
                {
                    InitError = "D3D12 Device required for Fence creation.";
                    return;
                }

                if (FAILED(hrFence))
                {
                    InitError = $"Failed to create Fence (0x{hrFence:X})";
                    return;
                }

                _fenceEvent = CreateEventW(null, FALSE, FALSE, null);
                IsSupported = true;
            }
            catch (Exception ex)
            {
                IsSupported = false;
                InitError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private bool TryCreateQueue(ushort capacity, IntPtr device, Guid uuidQueue, out int hr)
        {
            DSTORAGE_QUEUE_DESC desc = new DSTORAGE_QUEUE_DESC();
            desc.SourceType = DStorageConstants.DSTORAGE_REQUEST_SOURCE_MEMORY;
            desc.Capacity = capacity;
            desc.Priority = DStorageConstants.DSTORAGE_PRIORITY_NORMAL;
            desc.Name = IntPtr.Zero;
            desc.Device = device;

            fixed(IDStorageQueue** ppQueue = &_queue)
            {
                hr = _factory->CreateQueue(&desc, &uuidQueue, (void**)ppQueue);
            }
            return SUCCEEDED(hr);
        }

        public double RunDecompressionBatch(byte[] compressedData, int[] chunkSizes, long[] chunkOffsets, int totalOriginalSize)
        {
            if (!IsSupported) return 0;

            ComPtr<ID3D12Resource> dstBuffer = default;
            void* sysMemBuffer = null;

            try
            {
                if (IsHardwareAccelerated)
                {
                    D3D12_HEAP_PROPERTIES heapProps = new D3D12_HEAP_PROPERTIES(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT);

                    D3D12_RESOURCE_DESC resDesc = new D3D12_RESOURCE_DESC();
                    resDesc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
                    resDesc.Width = (ulong)totalOriginalSize;
                    resDesc.Height = 1;
                    resDesc.DepthOrArraySize = 1;
                    resDesc.MipLevels = 1;
                    resDesc.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
                    resDesc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
                    resDesc.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

                    Guid uuidRes = typeof(ID3D12Resource).GUID;
                    if (FAILED(_device.Get()->CreateCommittedResource(
                        &heapProps,
                        D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                        &resDesc,
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                        null,
                        &uuidRes,
                        (void**)dstBuffer.GetAddressOf())))
                    {
                        throw new Exception("Failed to allocate GPU memory");
                    }
                }
                else
                {
                    sysMemBuffer = NativeMemory.Alloc((nuint)totalOriginalSize);
                }

                fixed (byte* pData = compressedData)
                {
                    ulong currentOutputOffset = 0;
                    long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                    int requestIndex = 0;
                    while (requestIndex < chunkSizes.Length)
                    {
                        int batchCount = 0;
                        while (requestIndex < chunkSizes.Length && batchCount < 32)
                        {
                            int cSize = chunkSizes[requestIndex];
                            long cOffset = chunkOffsets[requestIndex];

                            uint uncompressedChunkSize = 65536;
                            if (currentOutputOffset + uncompressedChunkSize > (ulong)totalOriginalSize)
                                uncompressedChunkSize = (uint)((ulong)totalOriginalSize - currentOutputOffset);

                            DSTORAGE_REQUEST request = new DSTORAGE_REQUEST();
                            request.SetOptions(
                                DStorageConstants.DSTORAGE_REQUEST_SOURCE_MEMORY,
                                IsHardwareAccelerated ? DStorageConstants.DSTORAGE_REQUEST_DESTINATION_BUFFER : DStorageConstants.DSTORAGE_REQUEST_DESTINATION_MEMORY,
                                DStorageConstants.DSTORAGE_COMPRESSION_FORMAT_GDEFLATE
                            );

                            request.Source.Memory.Source = pData + cOffset + 4;
                            request.Source.Memory.Size = (uint)cSize;

                            if (IsHardwareAccelerated)
                            {
                                request.Destination.Buffer.Resource = dstBuffer.Get();
                                request.Destination.Buffer.Offset = currentOutputOffset;
                                request.Destination.Buffer.Size = uncompressedChunkSize;
                            }
                            else
                            {
                                request.Destination.Memory.Buffer = (byte*)sysMemBuffer + currentOutputOffset;
                                request.Destination.Memory.Size = uncompressedChunkSize;
                            }

                            request.UncompressedSize = uncompressedChunkSize;

                            _queue->EnqueueRequest(&request);

                            currentOutputOffset += uncompressedChunkSize;
                            requestIndex++;
                            batchCount++;
                        }

                        _fenceValue++;
                        _queue->EnqueueSignal(_fence.Get(), _fenceValue);
                        _queue->Submit();

                        if (_fence.Get()->GetCompletedValue() < _fenceValue)
                        {
                            _fence.Get()->SetEventOnCompletion(_fenceValue, _fenceEvent);
                            WaitForSingleObject(_fenceEvent, INFINITE);
                        }
                    }

                    long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    return (double)(endTicks - startTicks) / System.Diagnostics.Stopwatch.Frequency;
                }
            }
            finally
            {
                if (dstBuffer.Get() != null) dstBuffer.Dispose();
                if (sysMemBuffer != null) NativeMemory.Free(sysMemBuffer);
            }
        }

        public void Dispose()
        {
            if (_fenceEvent.Value != null) CloseHandle(_fenceEvent);
            if (_queue != null) { _queue->Release(); _queue = null; }
            if (_factory != null) { _factory->Release(); _factory = null; }
            if (_hDStorage != IntPtr.Zero) { FreeLibrary(_hDStorage); _hDStorage = IntPtr.Zero; }
            _fence.Dispose();
            _device.Dispose();
        }
    }
}