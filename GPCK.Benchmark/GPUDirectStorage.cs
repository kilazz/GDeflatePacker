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
        public const int DSTORAGE_STAGING_BUFFER_SIZE_32MB = 32 * 1024 * 1024;
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

    // DXGI Helper structures for manual adapter selection
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    internal unsafe struct IDXGIFactory1
    {
#pragma warning disable 0649
        public void** lpVtbl;
#pragma warning restore 0649
        public int EnumAdapters1(uint Adapter, IDXGIAdapter1** ppAdapter) {
            return ((delegate* unmanaged[Stdcall]<IDXGIFactory1*, uint, IDXGIAdapter1**, int>)lpVtbl[7])((IDXGIFactory1*)Unsafe.AsPointer(ref this), Adapter, ppAdapter);
        }
        public uint Release() {
            return ((delegate* unmanaged[Stdcall]<IDXGIFactory1*, uint>)lpVtbl[2])((IDXGIFactory1*)Unsafe.AsPointer(ref this));
        }
    }

    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    internal unsafe struct IDXGIAdapter1
    {
#pragma warning disable 0649
        public void** lpVtbl;
#pragma warning restore 0649
        public int GetDesc1(DXGI_ADAPTER_DESC1* pDesc) {
            return ((delegate* unmanaged[Stdcall]<IDXGIAdapter1*, DXGI_ADAPTER_DESC1*, int>)lpVtbl[10])((IDXGIAdapter1*)Unsafe.AsPointer(ref this), pDesc);
        }
        public uint Release() {
            return ((delegate* unmanaged[Stdcall]<IDXGIAdapter1*, uint>)lpVtbl[2])((IDXGIAdapter1*)Unsafe.AsPointer(ref this));
        }
    }

    public unsafe class GpuDirectStorage : IDisposable
    {
        // 0x887A0002 is DXGI_ERROR_NOT_FOUND
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("dstorage.dll", EntryPoint = "DStorageSetConfiguration1", ExactSpelling = true)]
        private static extern int DStorageSetConfiguration1(DSTORAGE_CONFIGURATION1* config);

        [DllImport("dstorage.dll", EntryPoint = "DStorageGetFactory", ExactSpelling = true)]
        private static extern int DStorageGetFactory(Guid* riid, void** ppv);

        [DllImport("dxgi", SetLastError = true)]
        private static extern int CreateDXGIFactory1(Guid* riid, void** ppFactory);

        private ComPtr<ID3D12Device> _device;
        private IDStorageFactory* _factory = null;
        private IDStorageQueue* _queue = null;
        private ComPtr<ID3D12Fence> _fence;
        private ulong _fenceValue = 0;
        private HANDLE _fenceEvent;

        public bool IsSupported { get; private set; } = false;
        public bool IsHardwareAccelerated { get; private set; } = false;
        public string InitError { get; private set; } = "";

        private static string _staticInitError = "";

        // Static initializer to configure DirectStorage exactly once before any factory is created
        static GpuDirectStorage()
        {
            try
            {
                // Preload compiler dependencies.
                // dstoragecore.dll relies on d3dcompiler_47.dll or dxil.dll/dxcompiler.dll.

                string currentDir = AppContext.BaseDirectory;

                // Load D3D12Core.dll if present to prefer Agility SDK
                string d3d12CorePath = Path.Combine(currentDir, "D3D12Core.dll");
                if (File.Exists(d3d12CorePath)) LoadLibraryW(d3d12CorePath);

                // Load compilers
                LoadLibraryW("d3dcompiler_47.dll");
                LoadLibraryW("dxil.dll");
                LoadLibraryW("dxcompiler.dll");

                // Configuration for DirectStorage
                DSTORAGE_CONFIGURATION1 config = new DSTORAGE_CONFIGURATION1();

                config.NumSubmitThreads = 0; // Use default
                config.NumBuiltInCpuDecompressionThreads = 0; // Use default

                config.ForceLegacyMapping = 0;
                config.DisableBypassIO = 0;
                config.DisableTelemetry = 1;

                // Allow hardware to try first. If we force software (1) and it fails, it means compiler missing.
                // If we allow hardware (0), and it fails, it falls back to software.
                config.DisableGpuDecompressionMetacommand = 0;
                config.DisableGpuDecompressionSystemMemoryFallback = 0;

                int hr = DStorageSetConfiguration1(&config);
                if (FAILED(hr))
                {
                    _staticInitError += $" DStorageSetConfiguration1 failed: 0x{hr:X}";
                }
            }
            catch (Exception ex)
            {
                _staticInitError = $"Exception during static init: {ex.Message}";
            }
        }

        public GpuDirectStorage()
        {
            if (!string.IsNullOrEmpty(_staticInitError))
            {
                InitError = _staticInitError;
                IsSupported = false;
                return;
            }

            try
            {
                // 1. Get Factory
                Guid uuidFactory = new Guid("6924ea0c-c3cd-4826-b10a-f64f4ed927c1");
                int hrFactory;
                fixed(IDStorageFactory** ppFactory = &_factory)
                {
                    hrFactory = DStorageGetFactory(&uuidFactory, (void**)ppFactory);
                }

                if (FAILED(hrFactory))
                {
                    InitError = $"Failed to get DStorage Factory (0x{hrFactory:X}). Ensure dstorage.dll is in the output folder.";
                    return;
                }

                // 2. Debug Flags & Staging
                // Enable debug break on error
                // _factory->SetDebugFlags(0x1); // DSTORAGE_DEBUG_SHOW_ERRORS

                // Use 32MB Staging Buffer.
                _factory->SetStagingBufferSize(DStorageConstants.DSTORAGE_STAGING_BUFFER_SIZE_32MB);

                // 3. Create D3D12 Device
                IDXGIFactory1* pDXGIFactory = null;
                Guid uuidDxgi = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

                CreateDXGIFactory1(&uuidDxgi, (void**)&pDXGIFactory);

                IDXGIAdapter1* pAdapter = null;
                IDXGIAdapter1* pBestAdapter = null;
                nuint maxVideoMemory = 0;

                if (pDXGIFactory != null)
                {
                    uint i = 0;
                    while (pDXGIFactory->EnumAdapters1(i, &pAdapter) != DXGI_ERROR_NOT_FOUND)
                    {
                        DXGI_ADAPTER_DESC1 desc;
                        pAdapter->GetDesc1(&desc);

                        if ((desc.Flags & 2) == 0) // Skip Software
                        {
                            if (desc.DedicatedVideoMemory > maxVideoMemory)
                            {
                                if (pBestAdapter != null) pBestAdapter->Release();
                                pBestAdapter = pAdapter;
                                maxVideoMemory = desc.DedicatedVideoMemory;
                            }
                            else
                            {
                                pAdapter->Release();
                            }
                        }
                        else
                        {
                            pAdapter->Release();
                        }
                        i++;
                    }
                    pDXGIFactory->Release();
                }

                Guid uuidDevice = typeof(ID3D12Device).GUID;
                int hrDevice = D3D12CreateDevice((IUnknown*)pBestAdapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_0, &uuidDevice, (void**)_device.GetAddressOf());

                if (pBestAdapter != null) pBestAdapter->Release();

                if (FAILED(hrDevice))
                {
                    InitError = $"Failed to create D3D12 Device (0x{hrDevice:X}). Verify D3D12 support.";
                    return;
                }

                // 4. Check Wave Intrinsics (Required for GDeflate)
                D3D12_FEATURE_DATA_D3D12_OPTIONS1 options1 = new D3D12_FEATURE_DATA_D3D12_OPTIONS1();
                _device.Get()->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS1, &options1, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS1));

                if (options1.WaveOps == 0)
                {
                    InitError = "GPU does not support Wave Intrinsics (required for GDeflate).";
                    return;
                }

                // 5. Create Queue
                Guid uuidQueue = new Guid("cfdbd83f-9e06-4fda-83ef-647f42b59529");
                int hrQueue = -1;

                if (TryCreateQueue(DStorageConstants.DSTORAGE_MAX_QUEUE_CAPACITY, (IntPtr)_device.Get(), uuidQueue, out hrQueue))
                {
                    IsHardwareAccelerated = true;
                }
                else
                {
                    // Fallback
                    if (TryCreateQueue(DStorageConstants.DSTORAGE_MIN_QUEUE_CAPACITY, (IntPtr)_device.Get(), uuidQueue, out hrQueue))
                    {
                        IsHardwareAccelerated = true;
                    }
                    else
                    {
                        // 0x89240003 = DSTORAGE_ERROR_CORE_INIT_FAILED
                        // This typically means the decompression shader couldn't be compiled/loaded.
                        InitError = $"Failed to create Hardware DStorage Queue (HR: 0x{hrQueue:X}).\nUsually caused by missing 'd3dcompiler_47.dll' or 'dxcompiler.dll'.";
                        return;
                    }
                }

                // 6. Create Fence
                Guid uuidFence = typeof(ID3D12Fence).GUID;
                _device.Get()->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, &uuidFence, (void**)_fence.GetAddressOf());

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
            _fence.Dispose();
            _device.Dispose();
        }
    }
}
