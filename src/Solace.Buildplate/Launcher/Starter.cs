using Serilog;
using Solace.Buildplate.Connector.Model;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.Buildplate.Launcher;

public sealed class Starter
{
	private readonly EventBusClient _eventBusClient;
	private readonly string _eventBusConnectionString;
	private readonly string _publicAddress;

	public const int BRIDGE_PORT = 19132;
	public string PublicAddress => _publicAddress;

	public Starter(EventBusClient eventBusClient, string eventBusConnectionString, string publicAddress)
	{
		_eventBusClient = eventBusClient;
		_eventBusConnectionString = eventBusConnectionString;
		_publicAddress = publicAddress;
	}

	public Instance? StartInstance(
		string instanceId,
		string? playerId,
		string buildplateId,
		Instance.BuildplateSource buildplateSource,
		bool survival,
		bool night,
		bool saveEnabled,
		InventoryType inventoryType,
		long? shutdownTime
	)
	{
		DirectoryInfo? baseDir = CreateInstanceBaseDir(instanceId);
		if (baseDir is null)
		{
			return null;
		}

		var instance = Instance.Run(_eventBusClient, playerId, buildplateId, buildplateSource, instanceId, survival, night, saveEnabled, inventoryType, shutdownTime, baseDir, _eventBusConnectionString);

		Task.Run(async () =>
		{
			await instance.WaitForShutdownAsync();

			try
			{
				baseDir.Delete(recursive: true);
			}
			catch (Exception exception)
			{
				Log.Error(exception, $"Exception while cleaning up runtime directory: {exception.Message}");
			}
		}).Forget();

		return instance;
	}

	private static DirectoryInfo? CreateInstanceBaseDir(string instanceId)
	{
		var file = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"vienna-buildplate-instance_{instanceId}"));
		if (!file.TryCreate())
		{
			Log.Error($"Error creating instance base directory for {instanceId}");
			return null;
		}

		Log.Debug($"Created instance base directory {file.FullName}");
		return file;
	}
}
