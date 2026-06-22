using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;           
using Microsoft.Windows.AppLifecycle;

namespace ModManager;

public static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		try{

			if (args.Length > 0 && args[0] == "--elevated-inject")
			{
				RunElevatedInjection(args);
				return;
			}

			const string instanceKey = "ModManager_Main_Instance_Key";
			AppInstance mainInstance = AppInstance.FindOrRegisterForKey(instanceKey);

			if (!mainInstance.IsCurrent)
			{
				AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
				mainInstance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
				return;
			}
			SQLitePCL.Batteries_V2.Init();
			Microsoft.UI.Xaml.Application.Start(_ =>
			{
				DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));
				new App();
			});
		}
		catch (Exception ex)
		{

			Environment.Exit(-1);
		}

	}
	private static void RunElevatedInjection(string[] args)
	{
		
	}
}