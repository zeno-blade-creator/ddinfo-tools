using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevilDaggersInfo.Tools.NativeInterface.Services.Windows;

internal sealed class WindowsMemoryService : INativeMemoryService
{
	private readonly byte[] _pointerBuffer = new byte[sizeof(long)];

	public bool RequiresMarkerOffset => true;

	public BlockAddressResult ResolveBlockAddress(Process process, long? ddstatsMarkerOffset)
	{
		if (process.MainModule == null || ddstatsMarkerOffset is not { } markerOffset)
			return BlockAddressResult.Unresolved;

		_pointerBuffer.AsSpan().Clear();
		ReadMemory(process, process.MainModule.BaseAddress.ToInt64() + markerOffset, _pointerBuffer, 0, sizeof(long));
		return BlockAddressResult.Resolved(BitConverter.ToInt64(_pointerBuffer));
	}

	public Process? GetDevilDaggersProcess()
	{
		Process[] ddProcesses = Process.GetProcessesByName("dd");
		return Array.Find(ddProcesses, p => p.MainWindowTitle == "Devil Daggers");
	}

	public void ReadMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		ReadProcessMemory(process.Handle, new IntPtr(address), bytes, (uint)size, out _);
	}

	public void WriteMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		WriteProcessMemory(process.Handle, new IntPtr(address), bytes, (uint)size, out _);
	}

	// TODO: Use source-generated P/Invoke.
	[DllImport("kernel32.dll")]
	private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [In, Out] byte[] buffer, uint size, out uint lpNumberOfBytesRead);

	// TODO: Use source-generated P/Invoke.
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [In, Out] byte[] buffer, uint size, out uint lpNumberOfBytesWritten);
}
