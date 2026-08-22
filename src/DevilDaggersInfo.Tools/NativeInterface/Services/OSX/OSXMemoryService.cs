using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DevilDaggersInfo.Tools.NativeInterface.Services.OSX;

internal sealed partial class OSXMemoryService(ILogger logger) : INativeMemoryService
{
	private const int _kernSuccess = 0;
	private const int _kernInvalidArgument = 4;
	private const int _kernFailure = 5;

	private const int _vmProtRead = 0x01;
	private const int _vmRegionBasicInfo64 = 9;
	private const int _vmRegionBasicInfoCount64 = 9;

	private bool _loggedWriteFailure;
	private bool _loggedReadFailure;
	private bool _loggedProcessLookupFailure;
	private bool _loggedTaskFailure;

	// task_for_pid is privileged and comparatively expensive, so the port is acquired once and kept for as long as it
	// belongs to the process we are asked about. _taskPort is 0 when nothing is cached.
	private uint _taskPort;
	private int _taskPortProcessId;

	public void WriteMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		if (_loggedWriteFailure)
			return;

		_loggedWriteFailure = true;
		logger.Error("Could not write game memory: macOS memory writing is not implemented yet.");
	}

	public void ReadMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		// Callers can request an empty read (for example when the game reports a replay length of 0), which must not
		// be turned into a pointer into an empty array.
		if (size <= 0)
			return;

		if (!TryGetTaskPort(process, out uint task))
		{
			// A read that never happened leaves the buffer holding whatever was in it before, which the callers would
			// happily parse as game state. Clear it so they observe zeroes instead of stale data.
			Array.Clear(bytes, offset, size);
			return;
		}

		ulong bytesRead = 0;
		int kernReturn;
		unsafe
		{
			fixed (byte* localBase = &bytes[offset])
			{
				kernReturn = MachVmReadOverwrite(task, (ulong)address, (ulong)size, (ulong)localBase, ref bytesRead);
			}
		}

		if (kernReturn == _kernSuccess && bytesRead == (ulong)size)
		{
			_loggedReadFailure = false;
			return;
		}

		// A failed or partial read leaves the buffer holding whatever was in it before, which the callers would happily
		// parse as game state. Clear it so they observe zeroes instead of stale data. Refused reads are the common case
		// on macOS, so this matters more here than it does on Linux.
		Array.Clear(bytes, offset, size);

		if (kernReturn == _kernInvalidArgument)
		{
			// The task port itself is no longer valid - the game most likely exited. Drop it so the next read
			// re-acquires one instead of failing forever against a dead port.
			_taskPort = 0;
			_taskPortProcessId = 0;
		}

		if (_loggedReadFailure)
			return;

		_loggedReadFailure = true;
		if (kernReturn != _kernSuccess)
			logger.Error("Could not read game memory: mach_vm_read_overwrite returned kern_return_t {KernReturn} ({Description}) reading {Size} bytes at 0x{Address:X8}. {Region}", kernReturn, DescribeKernReturn(kernReturn), size, address, DescribeRegion(task, (ulong)address));
		else
			logger.Error("Could not read game memory: mach_vm_read_overwrite read {BytesRead} of {Size} requested bytes at 0x{Address:X8}.", bytesRead, size, address);
	}

	public Process? GetDevilDaggersProcess()
	{
		// The macOS process is named "Devil Daggers" - capitals and a space - where the Linux one is "devildaggers",
		// so the name has to be normalised before it can be compared.
		Process? process = Array.Find(Process.GetProcesses(), p => Normalize(p.ProcessName).StartsWith("devildaggers", StringComparison.Ordinal));
		if (process != null)
		{
			_loggedProcessLookupFailure = false;
			return process;
		}

		if (!_loggedProcessLookupFailure)
		{
			_loggedProcessLookupFailure = true;
			logger.Error("Could not locate the Devil Daggers process. Start Devil Daggers from Steam, then try again. If it is running, its process name is not 'Devil Daggers' and this build cannot find it.");
		}

		return null;
	}

	/// <summary>
	/// Lowercases and strips spaces so the macOS process name "Devil Daggers" compares equal to "devildaggers".
	/// </summary>
	private static string Normalize(string processName)
	{
		return processName.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
	}

	private bool TryGetTaskPort(Process process, out uint task)
	{
		if (_taskPort != 0 && _taskPortProcessId == process.Id)
		{
			task = _taskPort;
			return true;
		}

		// Anything cached belongs to a different process (the game was restarted); it is useless now.
		_taskPort = 0;
		_taskPortProcessId = 0;

		int kernReturn = TaskForPid(MachTaskSelf(), process.Id, out uint acquiredTask);
		if (kernReturn == _kernSuccess && acquiredTask != 0)
		{
			_taskPort = acquiredTask;
			_taskPortProcessId = process.Id;
			_loggedTaskFailure = false;
			task = acquiredTask;
			return true;
		}

		task = 0;

		if (_loggedTaskFailure)
			return false;

		_loggedTaskFailure = true;
		if (kernReturn == _kernFailure)
			logger.Error("Could not access the memory of Devil Daggers (process {ProcessId}): task_for_pid returned KERN_FAILURE (5), which on macOS means the request was not permitted. Quit ddinfo-tools and start it again under sudo - reading another process's memory requires root on macOS.", process.Id);
		else
			logger.Error("Could not access the memory of Devil Daggers (process {ProcessId}): task_for_pid returned kern_return_t {KernReturn} ({Description}). If ddinfo-tools was not started under sudo, start it again under sudo - reading another process's memory requires root on macOS.", process.Id, kernReturn, DescribeKernReturn(kernReturn));

		return false;
	}

	/// <summary>
	/// Describes the mapping the address falls in, so a refused read can be told apart from a read of memory the game
	/// never mapped in the first place.
	/// </summary>
	private static string DescribeRegion(uint task, ulong address)
	{
		ulong regionAddress = address;
		ulong regionSize = 0;
		int infoCount = _vmRegionBasicInfoCount64;

		int kernReturn;
		unsafe
		{
			int* info = stackalloc int[_vmRegionBasicInfoCount64];
			kernReturn = MachVmRegion(task, ref regionAddress, ref regionSize, _vmRegionBasicInfo64, info, ref infoCount, out _);

			if (kernReturn != _kernSuccess)
				return "The address lies above every mapped region in the process.";

			// mach_vm_region reports the first region at or above the requested address, so a higher start address
			// means the requested address is not mapped at all.
			if (regionAddress > address)
				return string.Create(CultureInfo.InvariantCulture, $"The address is not mapped; the nearest mapping starts at 0x{regionAddress:X8}.");

			// The first field of vm_region_basic_info_64 is the current protection.
			int protection = info[0];
			return (protection & _vmProtRead) == 0
				? string.Create(CultureInfo.InvariantCulture, $"The address lies in an unreadable region at 0x{regionAddress:X8} (protection 0x{protection:X}).")
				: string.Create(CultureInfo.InvariantCulture, $"The address lies in a readable region at 0x{regionAddress:X8} (protection 0x{protection:X}), so the read itself was refused.");
		}
	}

	private static string DescribeKernReturn(int kernReturn)
	{
		return kernReturn switch
		{
			1 => "KERN_INVALID_ADDRESS",
			2 => "KERN_PROTECTION_FAILURE",
			_kernInvalidArgument => "KERN_INVALID_ARGUMENT, usually a dead or unknown process",
			_kernFailure => "KERN_FAILURE, almost always 'not permitted'",
			_ => "see mach/kern_return.h",
		};
	}

	// macOS has no process_vm_readv equivalent; reading another process goes through Mach. These signatures were
	// verified against the running game. Mach calls report failure through their kern_return_t result rather than
	// errno, so there is nothing to gain from SetLastError here.
	[LibraryImport("libc", EntryPoint = "task_for_pid")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int TaskForPid(uint targetTport, int pid, out uint task);

	[LibraryImport("libc", EntryPoint = "mach_task_self")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial uint MachTaskSelf();

	[LibraryImport("libc", EntryPoint = "mach_vm_read_overwrite")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int MachVmReadOverwrite(uint task, ulong address, ulong size, ulong data, ref ulong outSize);

	[LibraryImport("libc", EntryPoint = "mach_vm_region")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static unsafe partial int MachVmRegion(uint task, ref ulong address, ref ulong size, int flavor, int* info, ref int infoCount, out uint objectName);
}
