using System;
using System.Globalization;
using System.IO;
using System.Management.Automation;

namespace Whisper
{
	/// <summary>
	/// <para type="synopsis">Write transcribe results into WebVTT format.</para>
	/// <para type="description">The format is documented there: https://en.wikipedia.org/wiki/WebVTT</para>
	/// </summary>
	/// <example><code>Export-WebVTT $transcribeResults -path transcript.vtt</code></example>
	[Cmdlet( VerbsData.Export, "WebVTT" )]
	public sealed class ExportWebVTT: ExportBase
	{
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

		static System.Collections.Generic.List<MergedSegment> mergeSegments( ReadOnlySpan<sSegment> segments )
		{
			var result = new System.Collections.Generic.List<MergedSegment>();
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

		static string printTime( TimeSpan ts ) =>
			ts.ToString( "hh':'mm':'ss'.'fff", CultureInfo.InvariantCulture );

		/// <summary>Write that text</summary>
		protected override void write( StreamWriter stream, TranscribeResult transcribeResult )
		{
			var segments = transcribeResult.segments;
			if( segments == null )
				return;

			stream.WriteLine( "WEBVTT" );
			stream.WriteLine();

			var merged = mergeSegments( segments );
			foreach( var seg in merged )
			{
				string begin = printTime( seg.Begin );
				string end = printTime( seg.End );
				stream.WriteLine( "{0} --> {1}", begin, end );
				stream.WriteLine( seg.Text );
				stream.WriteLine();
			}
		}
	}
}