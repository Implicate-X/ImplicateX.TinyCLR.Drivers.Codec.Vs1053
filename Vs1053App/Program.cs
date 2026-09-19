using GHIElectronics.TinyCLR.Pins;
using ImplicateX.Drivers.Codec;

namespace Vs1053App
{
	internal class Program
	{
		private static Vs1053 codec;

		static void Main()
		{
			_ = new Storage();

			codec = new Vs1053( 
				spiControllerName: FEZDuino.SpiBus.Spi6, 
				cmdCsPinID: FEZDuino.GpioPin.PC4, 
				datCsPinID: FEZDuino.GpioPin.PC5, 
				dreqPinID: FEZDuino.GpioPin.PC6, 
				resetPinID: FEZDuino.GpioPin.PC7 );

			codec.Initialize();
			codec.PlayMp3( @"A:\TEST.MP3" );
		}
	}
}
