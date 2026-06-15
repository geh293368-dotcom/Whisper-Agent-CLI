#include "textWriter.h"
#include "../../ComLightLib/comLightClient.h"
#include <array>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <pathcch.h>
#include <atlstr.h>
#include <atlfile.h>
#pragma comment(lib, "Pathcch.lib")

namespace
{
	HRESULT replaceExtension( CString& path, LPCTSTR inputPath, LPCTSTR ext )
	{
		path = inputPath;

		const size_t len = (size_t)path.GetLength() + 4;
		wchar_t* buffer = path.GetBufferSetLength( (int)len );
		const HRESULT hr = PathCchRenameExtension( buffer, len, ext );
		path.ReleaseBuffer();
		return hr;
	}

	// Abstract base class for text writers
	class Writer
	{
	protected:
		CAtlFile file;
		virtual HRESULT impl( const Whisper::sSegment* const segments, const size_t length ) = 0;

	public:
		HRESULT write( Whisper::iContext* context, LPCTSTR audioPath, LPCTSTR ext )
		{
			CString path;
			CHECK( replaceExtension( path, audioPath, ext ) );
			CHECK( file.Create( path, GENERIC_WRITE, 0, CREATE_ALWAYS ) );

			using namespace Whisper;

			const eResultFlags resultFlags = eResultFlags::Timestamps | eResultFlags::Tokens;
			ComLight::CComPtr<iTranscribeResult> result;
			CHECK( context->getResults( resultFlags, &result ) );

			sTranscribeLength len;
			CHECK( result->getSize( len ) );
			const sSegment* const segments = result->getSegments();

			return impl( segments, len.countSegments );
		}
	};

	HRESULT writeUtf8Bom( CAtlFile& file )
	{
		const std::array<uint8_t, 3> bom = { 0xEF, 0xBB, 0xBF };
		return file.Write( bom.data(), 3 );
	}

	void printTime( CStringA& rdi, Whisper::sTimeSpan time, bool comma = false )
	{
		Whisper::sTimeSpanFields fields = time;
		const uint32_t hours = fields.days * 24 + fields.hours;
		const char separator = comma ? ',' : '.';
		rdi.AppendFormat( "%02d:%02d:%02d%c%03d",
			(int)hours,
			(int)fields.minutes,
			(int)fields.seconds,
			separator,
			fields.ticks / 10'000 );
	}

	const char* skipBlank( const char* rsi )
	{
		while( true )
		{
			const char c = *rsi;
			if( c == ' ' || c == '\t' )
			{
				rsi++;
				continue;
			}
			return rsi;
		}
	}

	inline const char* cstr( const CStringA& s ) { return s; }

	HRESULT writeString( CAtlFile& file, const CStringA& line )
	{
		if( line.GetLength() > 0 )
			CHECK( file.Write( cstr( line ), (DWORD)line.GetLength() ) );
		return S_OK;
	}

	bool isContinuationByte( unsigned char c )
	{
		return ( c & 0xC0 ) == 0x80;
	}

	size_t nextCodePoint( const std::string& text, size_t offset )
	{
		if( offset >= text.size() )
			return text.size();
		offset++;
		while( offset < text.size() && isContinuationByte( (unsigned char)text[ offset ] ) )
			offset++;
		return offset;
	}

	std::string getComparisonText( const std::string& text )
	{
		std::string result;
		size_t offset = 0;
		while( offset < text.size() )
		{
			size_t next = nextCodePoint( text, offset );
			std::string cp = text.substr( offset, next - offset );
			if( cp != " " && cp != "," && cp != "." && cp != "!" && cp != "?" && cp != ";" && cp != ":" &&
				cp != "\xEF\xBC\x8C" && cp != "\xE3\x80\x82" && cp != "\xEF\xBC\x81" &&
				cp != "\xEF\xBC\x9F" && cp != "\xEF\xBC\x9B" && cp != "\xEF\xBC\x9A" && cp != "\xE3\x80\x81" )
			{
				if( cp.size() == 1 && cp[ 0 ] >= 'A' && cp[ 0 ] <= 'Z' )
					result += (char)( cp[ 0 ] + 32 );
				else
					result += cp;
			}
			offset = next;
		}
		return result;
	}

	struct MergedSegment
	{
		Whisper::sTimeSpan begin;
		Whisper::sTimeSpan end;
		std::string text;
	};

	std::vector<MergedSegment> mergeSegments( const Whisper::sSegment* const segments, const size_t length )
	{
		std::vector<MergedSegment> result;
		for( size_t i = 0; i < length; i++ )
		{
			const Whisper::sSegment& seg = segments[ i ];
			std::string text = skipBlank( seg.text );
			if( text.empty() )
				continue;

			Whisper::sTimeSpan begin = seg.time.begin;
			Whisper::sTimeSpan end = seg.time.end;
			if( end.ticks <= begin.ticks )
				end.ticks = begin.ticks + 8000000; // Enforce minimum duration of 800ms (8,000,000 ticks)

			if( !result.empty() )
			{
				auto& last = result.back();
				if( getComparisonText( last.text ) == getComparisonText( text ) )
				{
					last.end.ticks = std::max( last.end.ticks, end.ticks );
					continue;
				}
			}
			result.push_back( { begin, end, text } );
		}
		return result;
	}

	// Writer for UTF-8 text files
	class TextWriter : public Writer
	{
		const bool timestamps;

		HRESULT impl( const Whisper::sSegment* const segments, const size_t length ) override final
		{
			CHECK( writeUtf8Bom( file ) );
			using namespace Whisper;

			auto merged = mergeSegments( segments, length );
			CStringA line;
			for( const auto& seg : merged )
			{
				if( timestamps )
				{
					line = "[";
					printTime( line, seg.begin );
					line += " --> ";
					printTime( line, seg.end );
					line += "]  ";
				}
				else
					line = "";

				line += seg.text.c_str();
				line += "\r\n";
				CHECK( writeString( file, line ) );
			}
			return S_OK;
		}
	public:
		TextWriter( bool tt ) : timestamps( tt ) { }
	};

	// Writer for SubRip format: https://en.wikipedia.org/wiki/SubRip#SubRip_file_format
	class SubRipWriter : public Writer
	{
		HRESULT impl( const Whisper::sSegment* const segments, const size_t length ) override final
		{
			CHECK( writeUtf8Bom( file ) );
			using namespace Whisper;

			auto merged = mergeSegments( segments, length );
			CStringA line;
			for( size_t i = 0; i < merged.size(); i++ )
			{
				const auto& seg = merged[ i ];

				line.Format( "%zu\r\n", i + 1 );
				printTime( line, seg.begin, true );
				line += " --> ";
				printTime( line, seg.end, true );
				line += "\r\n";
				line += seg.text.c_str();
				line += "\r\n\r\n";
				CHECK( writeString( file, line ) );
			}
			return S_OK;
		}
	};

	// Writer for WebVTT format: https://en.wikipedia.org/wiki/WebVTT
	class VttWriter : public Writer
	{
		HRESULT impl( const Whisper::sSegment* const segments, const size_t length ) override final
		{
			CHECK( writeUtf8Bom( file ) );
			using namespace Whisper;

			CStringA line;
			line = "WEBVTT\r\n\r\n";
			CHECK( writeString( file, line ) );

			auto merged = mergeSegments( segments, length );
			for( const auto& seg : merged )
			{
				line = "";
				printTime( line, seg.begin );
				line += " --> ";
				printTime( line, seg.end );
				line += "\r\n";
				line += seg.text.c_str();
				line += "\r\n\r\n";
				CHECK( writeString( file, line ) );
			}
			return S_OK;
		}
	};
}

HRESULT writeText( Whisper::iContext* context, LPCTSTR audioPath, bool timestamps )
{
	TextWriter writer{ timestamps };
	return writer.write( context, audioPath, L".txt" );
}

HRESULT writeSubRip( Whisper::iContext* context, LPCTSTR audioPath )
{
	SubRipWriter writer;
	return writer.write( context, audioPath, L".srt" );
}

HRESULT writeWebVTT( Whisper::iContext* context, LPCTSTR audioPath )
{
	VttWriter writer;
	return writer.write( context, audioPath, L".vtt" );
}