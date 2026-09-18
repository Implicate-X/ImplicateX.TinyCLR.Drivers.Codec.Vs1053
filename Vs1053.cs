using System;
using System.IO;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace ImplicateX.Drivers.Codec
{
	/// <summary>
	/// Provides low-level control of a VS1053 audio codec over SPI using manually controlled chip-select lines.
	/// </summary>
	/// <remarks>
	/// This driver separates SCI (command) and SDI (data) traffic across two SPI device instances and
	/// gates transfers with the DREQ pin to follow VS1053 timing requirements.
	/// </remarks>
	public sealed class Vs1053
	{
		/// <summary>
		/// SCI/command channel index for the SPI device array.
		/// </summary>
		private const int CommandChannel = 0;
		/// <summary>
		/// SDI/data channel index for the SPI device array.
		/// </summary>
		private const int DataChannel = 1;
		/// <summary>
		/// Recommended clock frequency for the VS1053 chip in SCI_CLOCKF register.
		/// </summary>
		private const ushort RecommendedClockf = 0x6000;
		/// <summary>
		/// Startup sine frequency for the VS1053 chip.
		/// </summary>
		private const int StartupSineFrequency = 0x44;
		/// <summary>
		/// Startup SPI frequency for the VS1053 chip.
		/// </summary>
		private const int StartupSPIFrequency = 250_000;
		/// <summary>
		/// Data SPI frequency for the VS1053 chip.
		/// </summary>
		private const int DataSPIFrequency = 4_000_000;
		/// <summary>
		/// End fill byte for the VS1053 chip.
		/// </summary>
		private const byte EndFillByte = 0x00;
		/// <summary>
		/// Chunk size for data transfers to the VS1053 chip.
		/// </summary>
		private const int ChunkSize = 16;
		/// <summary>
		/// GPIO pin for the SCI/command channel chip-select.
		/// </summary>
		private readonly GpioPin cmdCsPin_;
		/// <summary>
		/// GPIO pin for the SDI/data channel chip-select.
		/// </summary>
		private readonly GpioPin datCsPin_;
		/// <summary>
		/// GPIO pin for the DREQ/data request signal.
		/// </summary>
		private readonly GpioPin dreqPin_;
		/// <summary>
		/// GPIO pin for the RESET signal.
		/// </summary>
		private readonly GpioPin resetPin_;
		/// <summary>
		/// SPI device for the SCI/command channel.
		/// </summary>
		private readonly SpiDevice spiCmdDevice_;
		/// <summary>
		/// SPI device for the SDI/data channel.
		/// </summary>
		private readonly SpiDevice spiDataDevice_;
		
		/// <summary>
		/// Synchronization object for SPI operations.
		/// </summary>
		private readonly object spiSync_ = new();
		/// <summary>
		/// Buffer for command transactions to the VS1053 chip.
		/// </summary>
		private readonly byte[] cmdBuffer_ = new byte[ 4 ];
		/// <summary>
		/// Buffer for data blocks to the VS1053 chip.
		/// </summary>
		private readonly byte[] dataBlock_ = new byte[ 32 ];
		/// <summary>
		/// Buffer for single-byte transactions to the VS1053 chip.
		/// </summary>
		private readonly byte[] singleByteBuffer_ = new byte[ 1 ];
		/// <summary>
		/// Startup mode for the VS1053 chip.
		/// </summary>
		private readonly ushort startupMode_;
		/// <summary>
		/// Indicates whether MIDI mode has been initialized.
		/// </summary>
		private bool midiModeInitialized_;

		private static class Register
		{
			/// <summary>
			/// SCI_MODE register address.
			/// </summary>
			public const byte Mode = 0x00;
			/// <summary>
			/// SCI_STATUS register address.
			/// </summary>
			public const byte Status = 0x01;
			/// <summary>
			/// SCI_BASS register address.
			/// </summary>
			public const byte BassTreble = 0x02;
			/// <summary>
			/// SCI_CLOCKF register address.
			/// </summary>
			public const byte ClockFrequency = 0x03;
			/// <summary>
			/// SCI_DECODE_TIME register address.
			/// </summary>
			public const byte DecodeTime = 0x04;
			/// <summary>
			/// SCI_AUDATA register address.
			/// </summary>
			public const byte AudioData = 0x05;
			/// <summary>
			/// SCI_WRAM register address for writing and reading to/from the VS1053's internal RAM.
			/// </summary>
			public const byte WRAMWriteRead = 0x06;
			/// <summary>
			/// SCI_WRAMADDR register address for setting the base address of the VS1053's internal RAM.
			/// </summary>
			public const byte WRAMBaseAddress = 0x07;
			/// <summary>
			/// SCI_STREAM_HEADER_DATA_0 register address.
			/// </summary>
			public const byte StreamHeaderData0 = 0x08;
			/// <summary>
			/// SCI_STREAM_HEADER_DATA_1 register address.
			/// </summary>
			public const byte StreamHeaderData1 = 0x09;
			/// <summary>
			/// SCI_APP_START_ADDRESS register address.
			/// </summary>
			public const byte AppStartAddress = 0x0A;
			/// <summary>
			/// SCI_VOLUME register address.
			/// </summary>
			public const byte Volume = 0x0B;
			/// <summary>
			/// SCI_APP_CONTROL_0 register address.
			/// </summary>
			public const byte AppControl0 = 0x0C;
			/// <summary>
			/// SCI_APP_CONTROL_1 register address.
			/// </summary>
			public const byte AppControl1 = 0x0D;
			/// <summary>
			/// SCI_APP_CONTROL_2 register address.
			/// </summary>
			public const byte AppControl2 = 0x0E;
			/// <summary>
			/// SCI_APP_CONTROL_3 register address.
			/// </summary>
			public const byte AppControl3 = 0x0F;
		}

		private static class Mode
		{
			/// <summary>
			/// Enables differential output mode for the analog output stage.
			/// </summary>
			public const ushort Differential = 0b000_0000_0000_0001;
			/// <summary>
			/// Allows MPEG Layers I and II decoding.
			/// </summary>
			public const ushort AllowMpeLayersIAndII = 0b0000_0000_0000_0010;
			/// <summary>
			/// Enables software reset of the VS1053 chip. This bit is self-clearing after the reset is complete.
			/// </summary>
			public const ushort SoftReset = 0b0000_0000_0000_0100;
			/// <summary>
			/// Cancels decoding of the current file.
			/// </summary>
			public const ushort CancelDecodingCurrentFile = 0b0000_0000_0000_1000;
			/// <summary>
			/// Sets the ear speaker to a low setting.
			/// </summary>
			public const ushort EarSpeakerLowSetting = 0b0000_0000_0001_0000;
			/// <summary>
			/// Allows SDI tests.
			/// </summary>
			public const ushort AllowSdiTests = 0b0000_0000_0010_0000;
			/// <summary>
			/// Enables stream mode.
			/// </summary>
			public const ushort StreamMode = 0b0000_0000_0100_0000;
			/// <summary>
			/// Sets the ear speaker to a high setting.
			/// </summary>
			public const ushort EarSpeakerHighSetting = 0b0000_0000_1000_0000;
			/// <summary>
			/// Sets the DCLK active edge.
			/// </summary>
			public const ushort DCLKActiveEdge = 0b0000_0001_0000_0000;
			/// <summary>
			/// Sets the bit order to MSb first.
			/// </summary>
			public const ushort BitOrderMSbFirst = 0b0000_0010_0000_0000;
			/// <summary>
			/// Shares the chip select.
			/// </summary>
			public const ushort ShareChipSelect = 0b0000_0100_0000_0000;
			/// <summary>
			/// Indicates a new SDI.
			/// </summary>
			public const ushort SdiNew = 0b0000_1000_0000_0000;
			/// <summary>
			/// Indicates that PCM recording is active.
			/// </summary>
			public const ushort PCMRecordingActive = 0b0001_0000_0000_0000;
			/// <summary>
			/// No specific mode is set.
			/// </summary>
			public const ushort None = 0b0010_0000_0000_0000;
			/// <summary>
			/// Selects Line 1.
			/// </summary>
			public const ushort Line1Selector = 0b0100_0000_0000_0000;
			/// <summary>
			/// SM_CLK_RANGE activates a clock divider in the XTAL input. <br/>
			/// When SM_CLK_RANGE is set, the clock is divided by 2 at the input.<br/>
			/// From the chip’s point of view e.g. 24 MHz becomes 12 MHz.<br/>
			/// SM_CLK_RANGE should be set as soon as possible after a chip reset.
			/// </summary>
			public const ushort InputClockRange = 0b1000_0000_0000_0000;

		}

		private static class Status
		{
			/// <summary>
			/// Reference voltage selection
			/// <list type="table">
			/// <item>0 = 1.23 V</item>
			/// <item>1 = 1.65 V</item>
			/// </list>
			/// </summary>
			public const ushort ReferenceVoltageSelection = 0b0000_0000_0000_0001;
			/// <summary>
			/// SS_AD_CLOCK can be set to divide the AD modulator frequency by 2 if XTALI/2 is too much.
			/// <list type="table">
			/// <item>0 = 6 MHz</item>
			/// <item>1 = 3 MHz</item>
			/// </list>
			/// </summary>
			public const ushort ClockSelection = 0b0000_0000_0000_0010;
			/// <summary>
			/// SS_APDOWN1 controls internal analog	powerdown.
			/// These bit are meant to be used by the system firmware only.
			/// </summary>
			public const ushort AnalogInternalPowerdown = 0b0000_0000_0000_0100;
			/// <summary>
			/// SS_APDOWN2 controls analog driver powerdown.
			/// </summary>
			public const ushort AnalogDriverPowerdown = 0b0000_0000_0000_1000;
			/// <summary>
			/// VersionMask is a 4-bit field that indicates the silicon version of the VS1053 chip.
			/// </summary>
			public const ushort VersionMask = 0b0000_0000_1111_0000;
			/// <summary>
			/// Overload is set when the analog output stage is overloaded.<br />
			/// The overload condition can be cleared by a soft reset or by clearing this bit in the SCI_MODE register.
			/// </summary>
			public const ushort Overload = 0b0000_0100_0000_0000;
			/// <summary>
			/// OverloadDisabled is set when the analog output stage overload is disabled.
			/// </summary>
			public const ushort OverloadDisabled = 0b0000_1000_0000_0000;
			/// <summary>
			/// SwingMask is a 3-bit field that indicates the swing level of the analog output stage.
			/// </summary>
			public const ushort SwingMask = 0b0111_0000_0000_0000;
			/// <summary>
			/// SS_DO_NOT_JUMP is set when a WAV, Ogg Vorbis, WMA, MP4, or AAC-ADIF header 
			/// is being decoded and jumping to another location in the file is not allowed.
			/// If you use soft reset or cancel, clear this bit yourself or it can be accidentally left set.
			/// </summary>
			public const ushort DecodedHeader = 0b1000_0000_0000_0000;
		}

		/// <summary>
		/// Creates a VS1053 driver instance and prepares GPIO/SPI resources for SCI (command) and SDI (data) communication.
		/// </summary>
		/// <param name="spiControllerName">SPI controller name used to open the VS1053 bus.</param>
		/// <param name="cmdCsPinID">GPIO pin number used as manual chip-select for the SCI/command channel.</param>
		/// <param name="datCsPinID">GPIO pin number used as manual chip-select for the SDI/data channel.</param>
		/// <param name="dreqPinID">GPIO pin number connected to VS1053 DREQ (data request) signal.</param>
		/// <param name="resetPinID">GPIO pin number connected to VS1053 hardware reset.</param>
		/// <remarks>
		/// The driver intentionally uses <see cref="SpiChipSelectType.None"/> and controls chip-select lines manually
		/// via GPIO to ensure stable VS1053 transactions on both channels.
		/// Startup SPI frequency is applied to both devices; higher SDI speed is configured later in <see cref="Initialize"/>.
		/// </remarks>
		public Vs1053( string spiControllerName, int cmdCsPinID, int datCsPinID, int dreqPinID, int resetPinID )
		{
			var gpioController = GpioController.GetDefault();
			var spiController = SpiController.FromName( spiControllerName );

			cmdCsPin_ = gpioController.OpenPin( cmdCsPinID );
			datCsPin_ = gpioController.OpenPin( datCsPinID );
			dreqPin_ = gpioController.OpenPin( dreqPinID );
			resetPin_ = gpioController.OpenPin( resetPinID );

			cmdCsPin_.SetDriveMode( GpioPinDriveMode.Output );
			datCsPin_.SetDriveMode( GpioPinDriveMode.Output );
			dreqPin_.SetDriveMode( GpioPinDriveMode.Input );
			resetPin_.SetDriveMode( GpioPinDriveMode.Output );

			DeselectCommand();
			DeselectData();

			resetPin_.Write( GpioPinValue.High );

			var spiSettings = new SpiConnectionSettings[]
			{
				new() 
				{
					ChipSelectType = SpiChipSelectType.None,
					ClockFrequency = StartupSPIFrequency,
					Mode = SpiMode.Mode0
				},
				new() 
				{
					ChipSelectType = SpiChipSelectType.None,
					ClockFrequency = StartupSPIFrequency,
					Mode = SpiMode.Mode0
				}
			};

			spiCmdDevice_ = spiController.GetDevice( spiSettings[ CommandChannel ] );
			spiDataDevice_ = spiController.GetDevice( spiSettings[ DataChannel ] );

			startupMode_ = Mode.SdiNew | Mode.AllowMpeLayersIAndII;
		}

		/// <summary>
		/// Resets and configures the codec for normal playback operation.
		/// </summary>
		/// <remarks>
		/// Performs a hardware reset, applies initial SCI register configuration, then increases SDI SPI clock
		/// from startup speed to streaming speed for audio data transfer.
		/// </remarks>
		public void Initialize()
		{
			ResetHardware();
			ConfigureStartupRegisters();

			spiDataDevice_.ConnectionSettings.ClockFrequency = DataSPIFrequency;
		}

		/// <summary>
		/// Sets output volume for left and right channels.
		/// </summary>
		/// <param name="leftChannel">Left channel volume from 0 (mute) to 255 (max).</param>
		/// <param name="rightChannel">Right channel volume from 0 (mute) to 255 (max).</param>
		/// <remarks>
		/// VS1053 SCI volume register is inverse-scaled; this method translates user-friendly values internally.
		/// </remarks>
		public void SetVolume( byte leftChannel, byte rightChannel )
		{
			ushort volume = ( ushort )( ( 255 - leftChannel ) << 8 | ( 255 - rightChannel ) );
			CommandWrite( Register.Volume, volume );
		}

		/// <summary>
		/// Runs the VS1053 startup sine test for a fixed duration.
		/// </summary>
		/// <param name="durationMs">Duration in milliseconds. Default is 3000.</param>
		/// <param name="sineCode">VS1053 sine test frequency code. Default is <c>0x44</c>.</param>
		public void RunStartupSineTest( int durationMs = 3000, byte sineCode = StartupSineFrequency )
		{
			StartSineTest( sineCode );
			Thread.Sleep( durationMs );
			StopSineTest();
		}

		/// <summary>
		/// Starts VS1053 sine test mode.
		/// </summary>
		/// <param name="sineCode">VS1053 sine test frequency code.</param>
		public void StartSineTest( byte sineCode = StartupSineFrequency )
		{
			ushort mode = (ushort)((Mode.SdiNew | Mode.AllowSdiTests) & unchecked(( ushort )~Mode.Line1Selector));
			CommandWrite( Register.Mode, mode );

			WriteStrictTestFrame( new byte[]
			{
				0x53, 0xEF, 0x6E, sineCode,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00
			} );
		}

		/// <summary>
		/// Stops VS1053 sine test mode and restores startup register configuration.
		/// </summary>
		public void StopSineTest()
		{
			WriteStrictTestFrame( new byte[]
			{
				0x45, 0x78, 0x69, 0x74,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00
			} );

			CommandWrite( Register.Mode, Mode.SdiNew );
			Thread.Sleep( 20 );
			ConfigureStartupRegisters();
			Thread.Sleep( 50 );
		}

		/// <summary>
		/// Attempts to play a MIDI note.
		/// </summary>
		/// <param name="note">MIDI note number.</param>
		/// <param name="velocity">MIDI velocity.</param>
		/// <param name="ms">Requested note duration in milliseconds.</param>
		/// <exception cref="NotSupportedException">
		/// Always thrown because MIDI-over-SPI is intentionally disabled in this hardware setup.
		/// </exception>
		public void PlayMIDINote( byte note, byte velocity, int ms )
		{
			throw new NotSupportedException( "MIDI over SPI is disabled for the Adafruit Music Maker setup. Use MP3 playback path or implement UART MIDI RX path." );
		}

		/// <summary>
		/// Attempts to play a built-in MIDI song.
		/// </summary>
		/// <exception cref="NotSupportedException">
		/// Always thrown because MIDI-over-SPI is intentionally disabled in this hardware setup.
		/// </exception>
		public void PlayMIDISong()
		{
			throw new NotSupportedException( "MIDI over SPI is disabled for the Adafruit Music Maker setup. Use MP3 playback path or implement UART MIDI RX path." );
		}

		/// <summary>
		/// Switches the codec into VS1053 real-time MIDI mode.
		/// </summary>
		/// <remarks>
		/// This method is idempotent and returns immediately when MIDI mode has already been initialized.
		/// It performs a hardware reset, reapplies the startup register configuration, waits for a stable
		/// DREQ signal, then writes the VS1053 real-time MIDI bootstrap sequence through SCI registers.
		/// If initialization times out, the sequence is retried once before the exception is allowed to propagate.
		/// On success, the analog output path is enabled and a default volume is applied.
		/// </remarks>
		/// <exception cref="TimeoutException">
		/// Thrown when the codec does not become ready during initialization after both retry attempts.
		/// </exception>
		private void EnterRealtimeMidiMode()
		{
			if( midiModeInitialized_ )
			{
				return;
			}

			for( int attempt = 0; attempt < 2; attempt++ )
			{
				try
				{
					ResetHardware();
					ConfigureStartupRegisters();
					WaitDreqStableHigh( 3, 1000 );
					Thread.Sleep( 20 );

					// VS1053 realtime MIDI bootstrap sequence
					CommandWriteWithRecovery( Register.WRAMBaseAddress, 0xC017 );
					CommandWriteWithRecovery( Register.WRAMWriteRead, 0x0000 );
					CommandWriteWithRecovery( Register.AudioData, 44101 );
					CommandWriteWithRecovery( Register.AppStartAddress, 0x0050 );
					Thread.Sleep( 50 );

					EnableAnalogPath();
					SetVolume( 250, 250 );
					midiModeInitialized_ = true;
					return;
				}
				catch( TimeoutException )
				{
					if( attempt == 1 )
					{
						throw;
					}

					Thread.Sleep( 50 );
				}
			}
		}

		/// <summary>
		/// Writes a real-time MIDI message to the codec.
		/// </summary>
		/// <param name="status">MIDI status byte.</param>
		/// <param name="data1">First MIDI data byte.</param>
		/// <param name="data2">Second MIDI data byte.</param>
		private void WriteMidiMessage( byte status, byte data1, byte data2 )
		{
			// Realtime MIDI over SDI requires a zero sync byte prefix
			WriteData( [ 0x00, status, data1, data2 ] );
		}

		/// <summary>
		/// Streams an MP3 file to the codec.
		/// </summary>
		/// <param name="filePath">Absolute or relative path to the MP3 file.</param>
		/// <exception cref="IOException">Thrown when the file cannot be opened or read.</exception>
		/// <exception cref="InvalidOperationException">Thrown when no valid media start position is found.</exception>
		public void PlayMp3( string filePath )
		{
			midiModeInitialized_ = false;
			PlayFileCore( filePath, "MP3" );
		}

		/// <summary>
		/// Streams a WAV file to the codec.
		/// </summary>
		/// <param name="filePath">Absolute or relative path to the WAV file.</param>
		/// <exception cref="IOException">Thrown when the file cannot be opened or read.</exception>
		public void PlayWav( string filePath )
		{
			midiModeInitialized_ = false;
			PlayFileCore( filePath, "WAV" );
		}

		/// <summary>
		/// Opens an audio file and streams its payload to the VS1053 decoder over SDI.
		/// </summary>
		/// <param name="filePath">Absolute or relative path to the source media file.</param>
		/// <param name="label">
		/// Media discriminator used by this method to select format-specific preprocessing.
		/// Supported values in current callers are <c>"MP3"</c> and <c>"WAV"</c>.
		/// </param>
		/// <exception cref="InvalidOperationException">
		/// Thrown when the computed media payload start offset is outside the file bounds.
		/// </exception>
		/// <remarks>
		/// For MP3 input, ID3/tag data is skipped via <see cref="FindMp3DataStart(FileStream)"/>.
		/// After payload transmission, decoder flush bytes are sent to let the codec finish decoding buffered frames.
		/// </remarks>
		private void PlayFileCore( string filePath, string label )
		{
			try
			{
				using var fs = new FileStream( filePath, FileMode.Open, FileAccess.Read );

				long dataStart = 0;
				if( label == "MP3" )
				{
					dataStart = FindMp3DataStart( fs );
				}

				if( dataStart >= fs.Length )
				{
					throw new InvalidOperationException( "Invalid media data start position." );
				}

				fs.Position = dataStart;

				ConfigureStartupRegisters();
				Thread.Sleep( 20 );
				StartSong();

				var chunk = new byte[ 512 ];
				int totalSent = 0;
				int read;

				while( ( read = fs.Read( chunk, 0, chunk.Length ) ) > 0 )
				{
					WriteData( chunk, 0, read );
					totalSent += read;
				}

				SendFillers( 2052 );
				Thread.Sleep( 100 );

				CommandRead( Register.StreamHeaderData0 );
				CommandRead( Register.StreamHeaderData1 );
				CommandRead( Register.DecodeTime );
			}
			catch( Exception )
			{
				throw;
			}
		}

		/// <summary>
		/// Prepares the decoder pipeline and sends initial filler bytes before audio streaming.
		/// </summary>
		public void StartSong()
		{
			// Ensure that the mode for MP3 playback is set correctly (Line Out, not Microphone!)
			ushort mode = (ushort)(startupMode_ & unchecked(( ushort )~Mode.Line1Selector));
			CommandWrite( Register.Mode, mode );
			Thread.Sleep( 10 );

			// Enable amplifier through analog path
			this.EnableAnalogPath();
			Thread.Sleep( 10 );

			// Send 10 filler bytes to prepare FIFO
			SendFillers( 10 );
		}

		/// <summary>
		/// Requests graceful decoding cancellation and drains the VS1053 input buffer.
		/// </summary>
		/// <remarks>
		/// Implements the VS10xx-recommended SM_CANCEL sequence with filler bytes and polling.
		/// </remarks>
		public void StopSong()
		{
			// Ensure that the mode is set correctly (Line Out, not Microphone!)
			ushort mode = CommandRead( Register.Mode );
			mode = ( ushort )( mode | Mode.CancelDecodingCurrentFile | Mode.SdiNew );
			mode &= unchecked(( ushort )~Mode.Line1Selector);  // Line1Selector MUST be 0!
			CommandWrite( Register.Mode, mode );
			Thread.Sleep( 10 );

			// Send 2052 filler bytes before stopping
			SendFillers( 2052 );

			// Poll until SM_CANCEL is cleared by the chip (max 20 iterations x 10ms)
			for( int i = 0; i < 20; i++ )
			{
				SendFillers( 32 );
				ushort modeReg = CommandRead( Register.Mode );
				if( ( modeReg & Mode.CancelDecodingCurrentFile ) == 0 )
				{
					// SM_CANCEL was cleared, song stopped correctly
					SendFillers( 2052 );
					Thread.Sleep( 10 );
					return;
				}
				Thread.Sleep( 10 );
			}

		}

		/// <summary>
		/// Issues a software reset via SCI_MODE and waits for DREQ readiness.
		/// </summary>
		public void SoftReset()
		{
			CommandWrite( Register.Mode, Mode.SdiNew | Mode.SoftReset );
			Thread.Sleep( 10 );
			AwaitDataRequest( 500 );
		}

		/// <summary>
		/// Sends raw audio data to SDI in codec-sized chunks.
		/// </summary>
		/// <param name="data">Input audio payload.</param>
		/// <remarks>
		/// If an ID3v2 header is detected at the beginning, it is skipped automatically.
		/// </remarks>
		public void SendData( byte[] data )
		{
			int offset = 0;

			// Remove ID3v2 tag
			if( data.Length > 10 && data[ 0 ] == 'I' && data[ 1 ] == 'D' && data[ 2 ] == '3' )
			{
				int tagSize = ( data[ 6 ] << 21 ) | ( data[ 7 ] << 14 ) | ( data[ 8 ] << 7 ) | data[ 9 ];
				offset = 10 + tagSize;
			}

			if( offset < 0 || offset >= data.Length )
			{
				offset = 0;
			}

			var clean = new byte[ data.Length - offset ];
			Array.Copy( data, offset, clean, 0, clean.Length );

			lock( spiSync_ )
			{
				int remaining = clean.Length;
				int pos = 0;

				SelectData();
				try
				{
					while( remaining > 0 )
					{
						AwaitDataRequest( 1500 );
						int chunkSize = Math.Min( ChunkSize, remaining );
						spiDataDevice_.Write( clean, pos, chunkSize );
						pos += chunkSize;
						remaining -= chunkSize;
					}
				}
				finally
				{
					DeselectData();
				}
			}
		}

		/// <summary>
		/// Sends a specified number of filler bytes to the VS1053 SDI channel.
		/// </summary>
		/// <param name="count">The number of filler bytes to send.</param>
		private void SendFillers( int count )
		{
			if( count <= 0 )
			{
				return;
			}

			for( int i = 0; i < ChunkSize; i++ )
			{
				dataBlock_[ i ] = EndFillByte;
			}

			lock( spiSync_ )
			{
				int remaining = count;
				while( remaining > 0 )
				{
					AwaitDataRequest( 1500 );
					int chunkSize = Math.Min( ChunkSize, remaining );
					SelectData();
					try
					{
						spiDataDevice_.Write( dataBlock_, 0, chunkSize );
					}
					finally
					{
						DeselectData();
					}
					remaining -= chunkSize;
				}
			}
		}

		/// <summary>
		/// Waits until DREQ is high or throws on timeout.
		/// </summary>
		/// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
		/// <exception cref="TimeoutException">Thrown if DREQ does not become high in time.</exception>
		public void AwaitDataRequest( int timeoutMs = 1000 )
		{
			int waited = 0;

			while( dreqPin_.Read() == GpioPinValue.Low )
			{
				Thread.Sleep( 1 );
				
				waited++;

				if( waited >= timeoutMs )
				{
					throw new TimeoutException( "VS1053 DREQ timeout." );
				}
			}
		}

		/// <summary>
		/// Writes baseline SCI register configuration used after reset and recovery paths.
		/// </summary>
		/// <remarks>
		/// Also clears the decoded-header status bit and enables analog output stage.
		/// </remarks>
		public void ConfigureStartupRegisters()
		{
			ushort mode = startupMode_;
			mode &= unchecked(( ushort )~Mode.SoftReset);
			mode &= unchecked(( ushort )~Mode.Line1Selector);
			CommandWrite( Register.Mode, mode );
			CommandWrite( Register.ClockFrequency, RecommendedClockf );
			CommandWrite( Register.BassTreble, 0x0000 );

			// Clear SS_DO_NOT_JUMP (bit 15 in SCI_STATUS) per VS10xx note after reset/cancel paths.
			ushort status = CommandRead( Register.Status );
			status &= unchecked(( ushort )~Status.DecodedHeader );
			CommandWrite( Register.Status, status );

			EnableAnalogPath();
			SetVolume( 250, 250 );
		}

		/// <summary>
		/// Performs a hardware reset of the VS1053 and waits for DREQ to become high.
		/// </summary>
		private void ResetHardware()
		{
			DeselectCommand();
			DeselectData();

			resetPin_.Write( GpioPinValue.Low );
			Thread.Sleep( 5 );
			resetPin_.Write( GpioPinValue.High );
			Thread.Sleep( 5 );

			PrimeSpi();
			WaitDreqStableHigh( 10, 2000 );
		}

		/// <summary>
		/// Clears analog power-down bits in SCI_STATUS to enable the analog output path.
		/// </summary>
		public void EnableAnalogPath()
		{
			ushort status = CommandRead( Register.Status );
			status &= unchecked(( ushort )~( Status.AnalogDriverPowerdown | Status.AnalogInternalPowerdown ));
			CommandWrite( Register.Status, status );
			Thread.Sleep( 2 );
		}

		/// <summary>
		/// Writes a 16-bit value to a VS1053 SCI register at the specified address.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <param name="data">The 16-bit value to write.</param>
		private void CommandWrite( byte address, ushort data )
		{
			AwaitDataRequest( 1500 );

			cmdBuffer_[ 0 ] = 0x02;
			cmdBuffer_[ 1 ] = address;
			cmdBuffer_[ 2 ] = ( byte )( data >> 8 );
			cmdBuffer_[ 3 ] = ( byte )data;

			lock( spiSync_ )
			{
				SelectCommand();

				try
				{
					spiCmdDevice_.Write( cmdBuffer_ );
				}
				finally
				{
					DeselectCommand();
				}
			}

			AwaitDataRequest( 1500 );
		}

		/// <summary>
		/// Writes a 16-bit value to a VS1053 SCI register at the specified address, with a single retry on timeout.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <param name="data">The 16-bit value to write.</param>
		private void CommandWriteWithRecovery( byte address, ushort data )
		{
			for( int attempt = 0; attempt < 2; attempt++ )
			{
				try
				{
					CommandWrite( address, data );
					return;
				}
				catch( TimeoutException )
				{
					if( attempt == 1 )
					{
						throw;
					}

					ResetHardware();
					ConfigureStartupRegisters();
					WaitDreqStableHigh( 3, 1000 );
					Thread.Sleep( 20 );
				}
			}
		}

		/// <summary>
		/// Reads a 16-bit value from a VS1053 SCI register at the specified address.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <returns>The 16-bit value read from the SCI register.</returns>
		private ushort CommandRead( byte address )
		{
			AwaitDataRequest( 1500 );

			cmdBuffer_[ 0 ] = 0x03;
			cmdBuffer_[ 1 ] = address;
			cmdBuffer_[ 2 ] = 0xFF;
			cmdBuffer_[ 3 ] = 0xFF;

			var read = new byte[ 4 ];

			lock( spiSync_ )
			{
				SelectCommand();

				try
				{
					spiCmdDevice_.TransferFullDuplex( cmdBuffer_, 0, cmdBuffer_.Length, read, 0, read.Length );
				}
				finally
				{
					DeselectCommand();
				}
			}

			AwaitDataRequest( 1500 );

			return ( ushort )( ( read[ 2 ] << 8 ) | read[ 3 ] );
		}

		/// <summary>
		/// Writes a byte array to the VS1053 SDI channel in chunks, waiting for DREQ readiness.	
		/// </summary>
		/// <param name="data">The byte array to write.</param>
		private void WriteData( byte[] data )
		{
			WriteData( data, 0, data.Length );
		}

		/// <summary>
		/// Writes a portion of a byte array to the VS1053 SDI channel in chunks, waiting for DREQ readiness.
		/// </summary>
		/// <param name="data">The byte array to write.</param>
		/// <param name="offset">The zero-based byte offset in the array at which to begin writing bytes.</param>
		/// <param name="count">The number of bytes to write.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when the offset or count is out of range.</exception>
		private void WriteData( byte[] data, int offset, int count )
		{
			if( count <= 0 )
			{
				return;
			}

			if( offset < 0 || count < 0 || offset + count > data.Length )
			{
				throw new ArgumentOutOfRangeException();
			}

			lock( spiSync_ )
			{
				int remaining = count;
				int position = offset;

				SelectData();

				try
				{
					while( remaining > 0 )
					{
						AwaitDataRequest( 1500 );

						int chunk = remaining > ChunkSize ? ChunkSize : remaining;
						Array.Copy( data, position, dataBlock_, 0, chunk );

						spiDataDevice_.Write( dataBlock_, 0, chunk );

						position += chunk;
						remaining -= chunk;
					}
				}
				finally
				{
					DeselectData();
				}
			}
		}

		/// <summary>
		/// Writes a strict test frame to the VS1053 SDI channel, one byte at a time, waiting for DREQ readiness before each byte.
		/// </summary>
		/// <param name="frame">The byte array representing the strict test frame to write.</param>
		/// <exception cref="ArgumentNullException">Thrown when the frame is null.</exception>
		private void WriteStrictTestFrame( byte[] frame )
		{
			if( frame == null )
			{
				throw new ArgumentNullException( nameof( frame ) );
			}

			AwaitDataRequest( 500 );

			lock( spiSync_ )
			{
				SelectData();

				try
				{
					for( int index = 0; index < frame.Length; index++ )
					{
						singleByteBuffer_[ 0 ] = frame[ index ];
						spiDataDevice_.Write( singleByteBuffer_ );
					}
				}
				finally
				{
					DeselectData();
				}
			}
		}

		/// <summary>
		/// Scans the beginning of an MP3 stream and returns the best data start offset.
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		/// <returns>Byte position of first MP3 frame sync or derived fallback offset.</returns>
		/// <remarks>
		/// The original stream position is restored before returning.
		/// </remarks>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		/// <returns>Byte position of first MP3 frame sync or derived fallback offset.</returns>
		private static long FindMp3DataStart( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				int scanLen = ( int )Math.Min( 64 * 1024, fs.Length );

				if( scanLen < 2 )
				{
					return 0;
				}

				fs.Position = 0;

				var probe = new byte[ scanLen ];
				int got = fs.Read( probe, 0, probe.Length );

				if( got < 2 )
				{
					return 0;
				}

				// Prefer the earliest MP3 frame sync. Metadata before that is skipped implicitly.
				for( int i = 0; i <= got - 2; i++ )
				{
					if( probe[ i ] == 0xFF && ( probe[ i + 1 ] & 0xE0 ) == 0xE0 )
					{
						return i;
					}
				}

				// Fallback: ID3 signature (if present in this stream layout)
				for( int i = 0; i <= got - 10; i++ )
				{
					if( probe[ i ] == ( byte )'I' && probe[ i + 1 ] == ( byte )'D' && probe[ i + 2 ] == ( byte )'3' )
					{
						int size = ( probe[ i + 6 ] << 21 ) | ( probe[ i + 7 ] << 14 ) | ( probe[ i + 8 ] << 7 ) | probe[ i + 9 ];
						long pos = i + 10 + size;
						if( pos < fs.Length )
						{
							return pos;
						}
					}
				}

				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Waits until DREQ remains high continuously for the requested stability window.
		/// </summary>
		/// <param name="stableMs">Required consecutive milliseconds with DREQ high.</param>
		/// <param name="timeoutMs">Maximum total wait time in milliseconds.</param>
		/// <exception cref="TimeoutException">Thrown if stable high is not reached in time.</exception>
		private void WaitDreqStableHigh( int stableMs, int timeoutMs )
		{
			int waited = 0;
			int stable = 0;

			while( waited < timeoutMs )
			{
				if( dreqPin_.Read() == GpioPinValue.High )
				{
					stable++;
					if( stable >= stableMs )
					{
						return;
					}
				}
				else
				{
					stable = 0;
				}

				Thread.Sleep( 1 );
				waited++;
			}

			throw new TimeoutException( "VS1053 DREQ not stably high." );
		}

		/// <summary>
		/// Sends dummy SPI transfers to warm up the bus after reset.
		/// </summary>
		private void PrimeSpi()
		{
			var tx = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
			var rx = new byte[ tx.Length ];

			lock( spiSync_ )
			{
				spiCmdDevice_.TransferFullDuplex( tx, 0, tx.Length, rx, 0, rx.Length );
				spiCmdDevice_.TransferFullDuplex( tx, 0, tx.Length, rx, 0, rx.Length );
			}
		}

		/// <summary>
		/// Selects the SCI/command channel by pulling its chip-select line low.
		/// </summary>
		private void SelectCommand()
		{
			cmdCsPin_.Write( GpioPinValue.Low );
		}

		/// <summary>
		/// Deselects the SCI/command channel by pulling its chip-select line high.
		/// </summary>
		private void DeselectCommand()
		{
			cmdCsPin_.Write( GpioPinValue.High );
		}

		/// <summary>
		/// Selects the SDI/data channel by pulling its chip-select line low.
		/// </summary>
		private void SelectData()
		{
			datCsPin_.Write( GpioPinValue.Low );
		}
		
		/// <summary>
		/// Deselects the SDI/data channel by pulling its chip-select line high.
		/// </summary>
		private void DeselectData()
		{
			datCsPin_.Write( GpioPinValue.High );
		}
	}
}
