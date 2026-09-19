using System.IO;
using System.Diagnostics;
using GHIElectronics.TinyCLR.Devices.Storage;
using GHIElectronics.TinyCLR.Devices.UsbHost;
using GHIElectronics.TinyCLR.Pins;
using GHIElectronics.TinyCLR.IO;

namespace Vs1053App
{
	public class Storage
	{
		private readonly UsbHostController usbHostController = UsbHostController.GetDefault();

		public Storage()
		{
			usbHostController.OnConnectionChangedEvent += UsbHostController_OnConnectionChangedEvent;
			usbHostController.Enable();

		}

		private void UsbHostController_OnConnectionChangedEvent( UsbHostController sender, DeviceConnectionEventArgs e )
		{
			switch( e.DeviceStatus )
			{
				case DeviceConnectionStatus.Connected:
					HandleConnect( e );
					break;
				case DeviceConnectionStatus.Disconnected:
					break;
				case DeviceConnectionStatus.Bad:
					break;
			}
		}

		void HandleConnect( DeviceConnectionEventArgs e )
		{
			switch( e.Type )
			{
				case BaseDevice.DeviceType.MassStorage:
					MountMassStorage();

					FileEnumerator files = new( @"A:\\", FileEnumFlags.Files );

					foreach( var file in files )
					{
						Debug.WriteLine( "Found file: " + file );

					}
					break;
			}
		}
		void MountMassStorage()
		{
			var storage = StorageController.FromName( SC20100.StorageController.UsbHostMassStorage );
			var drive = FileSystem.Mount( storage.Hdc );
			var info = new DriveInfo( drive.Name );

			Debug.WriteLine( "Volume: " + info.VolumeLabel );
			Debug.WriteLine( "Format: " + info.DriveFormat );
			Debug.WriteLine( "Total size: " + info.TotalSize );
			Debug.WriteLine( "Free space: " + info.TotalFreeSpace );
			Debug.WriteLine( "Root: " + info.RootDirectory );
		}
	}
}
