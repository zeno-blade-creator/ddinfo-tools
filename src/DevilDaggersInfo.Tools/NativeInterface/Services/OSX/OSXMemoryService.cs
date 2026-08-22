using Serilog;
using System.Diagnostics;

namespace DevilDaggersInfo.Tools.NativeInterface.Services.OSX;

internal sealed class OSXMemoryService(ILogger logger) : INativeMemoryService
{
	private bool _loggedWriteFailure;
	private bool _loggedReadFailure;
	private bool _loggedProcessLookupFailure;

	public void WriteMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		if (_loggedWriteFailure)
			return;

		_loggedWriteFailure = true;
		logger.Error("Could not write game memory: macOS memory writing is not implemented yet.");
	}

	public void ReadMemory(Process process, long address, byte[] bytes, int offset, int size)
	{
		// A skipped read leaves the buffer holding whatever was in it before, which callers would happily parse as
		// game state. Clear it so they observe zeroes instead of stale data.
		if (size > 0)
			Array.Clear(bytes, offset, size);

		if (_loggedReadFailure)
			return;

		_loggedReadFailure = true;
		logger.Error("Could not read game memory: macOS memory reading is not implemented yet.");
	}

	public Process? GetDevilDaggersProcess()
	{
		if (!_loggedProcessLookupFailure)
		{
			_loggedProcessLookupFailure = true;
			logger.Error("Could not locate the Devil Daggers process: macOS process lookup is not implemented yet.");
		}

		return null;
	}
}
