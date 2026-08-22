using Serilog;
using System.Diagnostics;

namespace DevilDaggersInfo.Tools.NativeInterface.Services.OSX;

internal sealed class OSXMemoryService(ILogger logger) : INativeMemoryService
{
	public void WriteMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		logger.Error("Could not write game memory: macOS memory writing is not implemented yet.");
		throw new NotImplementedException("OSXMemoryService.WriteMemory is not implemented yet.");
	}

	public void ReadMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		logger.Error("Could not read game memory: macOS memory reading is not implemented yet.");
		throw new NotImplementedException("OSXMemoryService.ReadMemory is not implemented yet.");
	}

	public Process? GetDevilDaggersProcess()
	{
		logger.Error("Could not locate the Devil Daggers process: macOS process lookup is not implemented yet.");
		throw new NotImplementedException("OSXMemoryService.GetDevilDaggersProcess is not implemented yet.");
	}
}
