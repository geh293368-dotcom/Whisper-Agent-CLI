namespace TranscribeCS;
using System.Runtime.InteropServices;
using Whisper;

enum eFileOpenMode: byte
{
	/// <summary>Decode chunks of audio directly from the file, as needed</summary>
	StreamFile,

	/// <summary>Decode the complete file into FP32 PCM buffer, transcribe from there</summary>
	BufferPCM,

	/// <summary>Load the complete input file into a buffer, decode chunks of audio from that memory buffer as needed</summary>
	BufferFile
}

static class Program
{
	static readonly eFileOpenMode openMode = eFileOpenMode.StreamFile;
	// static readonly eFileOpenMode openMode = eFileOpenMode.BufferPCM;
	// static readonly eFileOpenMode openMode = eFileOpenMode.BufferFile;

	static int Main( string[] args )
	{
		try
		{
			// dbgListGPUs();

			CommandLineArgs cla;
			try
			{
				cla = new CommandLineArgs( args );
			}
			catch( OperationCanceledException )
			{
				return 1;
			}

			if( cla.engine != eTranscribeEngine.D3D11 )
				return runWhisperCpp( cla );

			const eLoggerFlags loggerFlags = eLoggerFlags.UseStandardError | eLoggerFlags.SkipFormatMessage;
			Library.setLogSink( eLogLevel.Debug, loggerFlags );

			using iModel model = Library.loadModel( cla.model );
			int[]? prompt = null;
			if( !string.IsNullOrEmpty( cla.prompt ) )
				prompt = model.tokenize( cla.prompt );

			using Context context = model.createContext();
			cla.apply( ref context.parameters );
			// When there're multiple input files, assuming they're independent clips
			context.parameters.setFlag( eFullParamsFlags.NoContext, true );
			using iMediaFoundation mf = Library.initMediaFoundation();
			Transcribe transcribe = new Transcribe( cla );

			foreach( string audioFile in cla.fileNames )
			{
				if( openMode == eFileOpenMode.StreamFile )
				{
					using iAudioReader reader = mf.openAudioFile( audioFile, cla.diarize );
					context.runFull( reader, transcribe, null, prompt );
				}
				else if( openMode == eFileOpenMode.BufferPCM )
				{
					using iAudioBuffer buffer = mf.loadAudioFile( audioFile, cla.diarize );
					context.runFull( buffer, transcribe, prompt );
				}
				else if( openMode == eFileOpenMode.BufferFile )
				{
					byte[] buffer = File.ReadAllBytes( audioFile );
					using iAudioReader reader = mf.loadAudioFileData( buffer, cla.diarize );
					context.runFull( reader, transcribe, null, prompt );
				}

				// When asked to, produce these text files
				if( cla.output_txt )
					writeTextFile( context, audioFile );
				if( cla.output_srt )
					writeSubRip( context, audioFile, cla );
				if( cla.output_vtt )
					writeWebVTT( context, audioFile );
			}

			context.timingsPrint();
			return 0;
		}
		catch( Exception ex )
		{
			Console.WriteLine( ex.Message );
			return ex.HResult;
		}
	}

	static int runWhisperCpp( CommandLineArgs cla )
	{
		if( cla.diarize )
			throw new NotSupportedException( "The whisper.cpp cpu/cuda engines do not support --diarize. Use --engine d3d11 for stereo speaker diarization." );

		string libraryName = cla.engine switch
		{
			eTranscribeEngine.Cpu => "WhisperCppBackendCpu.dll",
			eTranscribeEngine.Cuda => "WhisperCppBackendCuda.dll",
			_ => throw new ArgumentOutOfRangeException( nameof( cla.engine ) )
		};

		using iMediaFoundation mf = Library.initMediaFoundation();
		using WhisperCppNative api = new WhisperCppNative( libraryName );
		using WhisperCppNative.ModelHandle model = api.LoadModel( cla.model );
		if( model.IsInvalid || !api.ModelReady( model ) )
			throw new InvalidOperationException( model.IsInvalid ? "whisper.cpp did not return a model handle." : api.GetError( model ) );

		Console.OutputEncoding = System.Text.Encoding.UTF8;
		TimeSpan consoleTimeOffset = TimeSpan.FromMilliseconds( Math.Max( 0, cla.offset_t_ms ) );
		WhisperCppNative.ProgressCallback progressCallback =
			( value, _ ) =>
			{
				if( cla.print_progress )
					Console.Error.WriteLine( "progress = {0}%", value );
			};
		WhisperCppNative.CancelCallback cancelCallback = _ => 0;
		WhisperCppNative.SegmentCallback segmentCallback =
			( begin, end, text, _ ) =>
			{
				string? value = Marshal.PtrToStringUTF8( text );
				if( string.IsNullOrWhiteSpace( value ) )
					return;

				if( cla.no_timestamps )
				{
					Console.Write( value );
					Console.Out.Flush();
					return;
				}

				TimeSpan beginTime = consoleTimeOffset + TimeSpan.FromMilliseconds( begin * 10 );
				TimeSpan endTime = consoleTimeOffset + TimeSpan.FromMilliseconds( end * 10 );
				Console.WriteLine( "[{0} --> {1}]  {2}", Transcribe.printTime( beginTime ), Transcribe.printTime( endTime ), value.Trim() );
			};

		foreach( string audioFile in cla.fileNames )
		{
			using iAudioBuffer audio = mf.loadAudioFile( audioFile );
			IntPtr samples = audio.getPcmMono();
			int sampleCount = audio.countSamples();
			TimeSpan timeOffset = TimeSpan.Zero;
			if( cla.offset_t_ms > 0 )
			{
				int offsetSamples = checked( cla.offset_t_ms * 16 );
				if( offsetSamples >= sampleCount )
					throw new ArgumentOutOfRangeException( nameof( cla.offset_t_ms ), "The requested offset is beyond the end of the audio file." );
				samples = IntPtr.Add( samples, offsetSamples * sizeof( float ) );
				sampleCount -= offsetSamples;
				timeOffset = TimeSpan.FromMilliseconds( cla.offset_t_ms );
			}
			if( cla.duration_ms > 0 )
				sampleCount = Math.Min( sampleCount, checked( cla.duration_ms * 16 ) );

			int result = api.Transcribe(
				model,
				samples,
				sampleCount,
				cla.language.getCode(),
				cla.translate,
				cla.prompt,
				progressCallback,
				segmentCallback,
				cancelCallback );

			if( result != 0 )
				throw new InvalidOperationException( api.GetError( model ) );

			List<MergedSegment> merged = mergeSegments( readWhisperCppSegments( api, model, timeOffset ) );
			if( cla.output_txt )
				writeTextFile( merged, audioFile );
			if( cla.output_srt )
				writeSubRip( merged, audioFile, cla );
			if( cla.output_vtt )
				writeWebVTT( merged, audioFile );
		}

		return 0;
	}

	struct MergedSegment
	{
		public TimeSpan Begin;
		public TimeSpan End;
		public string Text;
	}

	static string NormalizeForComparison( string text )
	{
		if( string.IsNullOrEmpty( text ) )
			return string.Empty;
		var sb = new System.Text.StringBuilder();
		foreach( char c in text )
		{
			if( char.IsLetterOrDigit( c ) )
				sb.Append( char.ToLowerInvariant( c ) );
		}
		return sb.ToString();
	}

	static List<MergedSegment> readWhisperCppSegments( WhisperCppNative api, WhisperCppNative.ModelHandle model, TimeSpan timeOffset )
	{
		var result = new List<MergedSegment>();
		int count = api.SegmentCount( model );
		for( int i = 0; i < count; i++ )
		{
			result.Add( new MergedSegment {
				Begin = timeOffset + TimeSpan.FromMilliseconds( api.SegmentBegin( model, i ) * 10 ),
				End = timeOffset + TimeSpan.FromMilliseconds( api.SegmentEnd( model, i ) * 10 ),
				Text = api.SegmentText( model, i )
			} );
		}
		return result;
	}

	static List<MergedSegment> mergeSegments( ReadOnlySpan<sSegment> segments )
	{
		var source = new List<MergedSegment>( segments.Length );
		foreach( sSegment seg in segments )
		{
			source.Add( new MergedSegment {
				Begin = seg.time.begin,
				End = seg.time.end,
				Text = seg.text ?? string.Empty
			} );
		}
		return mergeSegments( source );
	}

	static List<MergedSegment> mergeSegments( IEnumerable<MergedSegment> segments )
	{
		var result = new List<MergedSegment>();
		foreach( MergedSegment seg in segments )
		{
			string text = seg.Text.Trim();
			if( string.IsNullOrEmpty( text ) )
				continue;

			TimeSpan begin = seg.Begin;
			TimeSpan end = seg.End;
			if( end <= begin )
				end = begin + TimeSpan.FromMilliseconds( 800 ); // Enforce minimum duration of 800ms

			if( result.Count > 0 )
			{
				var last = result[ result.Count - 1 ];
				if( string.Equals( NormalizeForComparison( last.Text ), NormalizeForComparison( text ), StringComparison.OrdinalIgnoreCase ) )
				{
					result[ result.Count - 1 ] = new MergedSegment {
						Begin = last.Begin,
						End = end > last.Begin ? end : last.End,
						Text = last.Text
					};
					continue;
				}
			}
			result.Add( new MergedSegment { Begin = begin, End = end, Text = text } );
		}
		return result;
	}

	static void writeTextFile( Context context, string audioPath )
	{
		writeTextFile( mergeSegments( context.results().segments ), audioPath );
	}

	static void writeTextFile( IReadOnlyList<MergedSegment> merged, string audioPath )
	{
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".txt" ) );
		foreach( var seg in merged )
			stream.WriteLine( seg.Text );
	}

	static void writeSubRip( Context context, string audioPath, CommandLineArgs cliArgs )
	{
		writeSubRip( mergeSegments( context.results( eResultFlags.Timestamps ).segments ), audioPath, cliArgs );
	}

	static void writeSubRip( IReadOnlyList<MergedSegment> merged, string audioPath, CommandLineArgs cliArgs )
	{
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".srt" ) );
		for( int i = 0; i < merged.Count; i++ )
		{
			stream.WriteLine( i + 1 + cliArgs.offset_n );
			var seg = merged[ i ];
			string begin = Transcribe.printTimeWithComma( seg.Begin );
			string end = Transcribe.printTimeWithComma( seg.End );
			stream.WriteLine( "{0} --> {1}", begin, end );
			stream.WriteLine( seg.Text );
			stream.WriteLine();
		}
	}

	static void writeWebVTT( Context context, string audioPath )
	{
		writeWebVTT( mergeSegments( context.results( eResultFlags.Timestamps ).segments ), audioPath );
	}

	static void writeWebVTT( IReadOnlyList<MergedSegment> merged, string audioPath )
	{
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".vtt" ) );
		stream.WriteLine( "WEBVTT" );
		stream.WriteLine();
		foreach( var seg in merged )
		{
			string begin = Transcribe.printTime( seg.Begin );
			string end = Transcribe.printTime( seg.End );
			stream.WriteLine( "{0} --> {1}", begin, end );
			stream.WriteLine( seg.Text );
			stream.WriteLine();
		}
	}

	static void dbgListGPUs()
	{
		string[] list = Library.listGraphicAdapters();
		Console.WriteLine( "    Graphics Adapters:\n{0}", string.Join( "\n", list ) );
	}
}
