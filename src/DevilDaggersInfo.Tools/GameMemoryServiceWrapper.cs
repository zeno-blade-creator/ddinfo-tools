using DevilDaggersInfo.Tools.GameMemory;
using DevilDaggersInfo.Tools.Networking;
using DevilDaggersInfo.Tools.Networking.TaskHandlers;
using DevilDaggersInfo.Tools.Platforms;
using DevilDaggersInfo.Tools.Ui.Popups;
using DevilDaggersInfo.Web.ApiSpec.Tools.ProcessMemory;
using Serilog;

namespace DevilDaggersInfo.Tools;

internal sealed class GameMemoryServiceWrapper(
	PopupManager popupManager,
	GameMemoryService gameMemoryService,
	IPlatformSpecificValues platformSpecificValues,
	ILogger logger)
{
	private bool _tryDownloadMarker = true;

	public long? Marker { get; private set; }

	/// <summary>
	/// Scans game memory. On platforms that need the marker offset from the API, and where it is not known yet, fires the call to retrieve it and then returns false, because memory can't be scanned until the HTTP call has returned successfully.
	/// </summary>
	/// <returns>Whether memory could be scanned. Always true on platforms that have no marker offset to wait for; use <see cref="GameMemory.GameMemoryService.BlockAddressStatus" /> to find out how the scan itself went.</returns>
	public bool Scan()
	{
		// Not every platform locates the block by reading a pointer at an offset the API supplies; the ones that search
		// the game's memory for it have nothing to download and nothing to wait for.
		if (!gameMemoryService.RequiresMarkerOffset)
		{
			gameMemoryService.Initialize(null);
			gameMemoryService.Scan();

			return true;
		}

		if (!Marker.HasValue)
		{
			if (!_tryDownloadMarker)
				return false;

			InitializeMarker();
			return false;
		}

		// Always initialize the process, so we detach properly when the game exits.
		gameMemoryService.Initialize(Marker.Value);
		gameMemoryService.Scan();

		return true;
	}

	private void InitializeMarker()
	{
		// Workaround to prevent multiple popups from appearing during continuous retry.
		if (popupManager.IsAnyOpen)
			return;

		AsyncHandler.Run(SetMarker, () => FetchMarker.HandleAsync(platformSpecificValues.AppOperatingSystem));

		void SetMarker(ApiResult<GetMarker> getMarkerResult)
		{
			getMarkerResult.Match(
				onSuccess: getMarker => Marker = getMarker.Value,
				onError: apiError =>
				{
					logger.Error(apiError.Exception, "API error: {Message}", apiError.Message);

					const string message = """
						Could not fetch marker from the DevilDaggers.info API.

						The marker is required in order to scan the game's memory. This means that:
						- Custom leaderboards won't work.
						- Some features in the replay editor won't work.
						- Practice will still work, but you won't be able to see your stats.

						Do you want to retry?

						If you select "No", you will have to restart the app to try again.
						""";

					popupManager.ShowQuestion("Failed to retrieve marker", message, () => _tryDownloadMarker = true, () => _tryDownloadMarker = false);
				});
		}
	}
}
