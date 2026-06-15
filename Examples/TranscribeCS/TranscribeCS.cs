namespace TranscribeCS;
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

	static List<MergedSegment> mergeSegments( ReadOnlySpan<sSegment> segments )
	{
		var result = new List<MergedSegment>();
		foreach( sSegment seg in segments )
		{
			string text = seg.text?.Trim() ?? string.Empty;
			if( string.IsNullOrEmpty( text ) )
				continue;

			TimeSpan begin = seg.time.begin;
			TimeSpan end = seg.time.end;
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
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".txt" ) );
		var merged = mergeSegments( context.results().segments );
		foreach( var seg in merged )
			stream.WriteLine( seg.Text );
	}

	static void writeSubRip( Context context, string audioPath, CommandLineArgs cliArgs )
	{
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".srt" ) );
		var merged = mergeSegments( context.results( eResultFlags.Timestamps ).segments );

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
		using var stream = File.CreateText( Path.ChangeExtension( audioPath, ".vtt" ) );
		stream.WriteLine( "WEBVTT" );
		stream.WriteLine();

		var merged = mergeSegments( context.results( eResultFlags.Timestamps ).segments );
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