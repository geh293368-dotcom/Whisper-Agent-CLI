#include "SubtitlePipeline.h"

#include <algorithm>
#include <cstdio>
#include <limits>

namespace
{
	constexpr int64_t ticksPerMillisecond = 10'000;

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

	size_t codePointCount( const std::string& text )
	{
		size_t count = 0;
		for( size_t i = 0; i < text.size(); i = nextCodePoint( text, i ) )
			count++;
		return count;
	}

	bool isAsciiSpace( unsigned char c )
	{
		return c == ' ' || c == '\t' || c == '\r' || c == '\n';
	}

	std::string normalizeWhitespace( const std::string& source )
	{
		std::string result;
		result.reserve( source.size() );
		bool pendingSpace = false;
		for( unsigned char c : source )
		{
			if( isAsciiSpace( c ) )
			{
				pendingSpace = !result.empty();
				continue;
			}
			if( pendingSpace )
				result.push_back( ' ' );
			pendingSpace = false;
			result.push_back( (char)c );
		}
		return result;
	}

	bool isBreakPunctuation( const std::string& codePoint )
	{
		return codePoint == "," || codePoint == "." || codePoint == "!" || codePoint == "?" ||
			codePoint == ";" || codePoint == ":" || codePoint == "\xEF\xBC\x8C" ||
			codePoint == "\xE3\x80\x82" || codePoint == "\xEF\xBC\x81" ||
			codePoint == "\xEF\xBC\x9F" || codePoint == "\xEF\xBC\x9B" ||
			codePoint == "\xEF\xBC\x9A" || codePoint == "\xE3\x80\x81";
	}

	std::vector<std::string> splitChunks( const std::string& text, size_t maximumCharacters )
	{
		if( maximumCharacters == 0 || codePointCount( text ) <= maximumCharacters )
			return { text };

		std::vector<std::string> result;
		size_t begin = 0;
		while( begin < text.size() )
		{
			size_t cursor = begin;
			size_t lastPreferred = std::string::npos;
			size_t characters = 0;
			while( cursor < text.size() && characters < maximumCharacters )
			{
				const size_t next = nextCodePoint( text, cursor );
				const std::string cp = text.substr( cursor, next - cursor );
				if( cp == " " || isBreakPunctuation( cp ) )
					lastPreferred = next;
				cursor = next;
				characters++;
			}

			size_t end = cursor;
			if( cursor < text.size() && lastPreferred != std::string::npos && lastPreferred > begin )
				end = lastPreferred;
			std::string chunk = normalizeWhitespace( text.substr( begin, end - begin ) );
			if( !chunk.empty() )
				result.emplace_back( std::move( chunk ) );
			begin = end;
			while( begin < text.size() && text[ begin ] == ' ' )
				begin++;
		}
		return result;
	}

	std::string wrapLines( const std::string& text, size_t maximumCharacters )
	{
		const auto lines = splitChunks( text, maximumCharacters );
		std::string result;
		for( size_t i = 0; i < lines.size(); i++ )
		{
			if( i != 0 )
				result += "\r\n";
			result += lines[ i ];
		}
		return result;
	}

	std::string timestamp( int64_t ticks, bool comma, bool brackets )
	{
		if( ticks < 0 )
			ticks = 0;
		const int64_t totalMilliseconds = ticks / ticksPerMillisecond;
		const int milliseconds = (int)( totalMilliseconds % 1000 );
		const int64_t totalSeconds = totalMilliseconds / 1000;
		const int seconds = (int)( totalSeconds % 60 );
		const int minutes = (int)( ( totalSeconds / 60 ) % 60 );
		const int64_t hours = totalSeconds / 3600;
		char buffer[ 64 ];
		std::snprintf( buffer, sizeof( buffer ), brackets ? "[%02lld:%02d:%02d%c%03d]" : "%02lld:%02d:%02d%c%03d",
			(long long)hours, minutes, seconds, comma ? ',' : '.', milliseconds );
		return buffer;
	}
	std::string getComparisonText( const std::string& text )
	{
		std::string result;
		size_t offset = 0;
		while( offset < text.size() )
		{
			size_t next = nextCodePoint( text, offset );
			std::string cp = text.substr( offset, next - offset );
			if( cp != " " && !isBreakPunctuation( cp ) )
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
}

std::vector<Subtitle::Cue> Subtitle::build( const std::vector<SourceSegment>& source, const Options& options )
{
	std::vector<Cue> result;
	const size_t maxCueCharacters = (size_t)options.maxCharactersPerLine * std::max<uint32_t>( 1, options.maxLines );
	int64_t previousEnd = -options.minimumGap;

	std::vector<SourceSegment> mergedSource;
	for ( const auto& input : source )
	{
		std::string normalized = normalizeWhitespace( input.text );
		if ( normalized.empty() )
			continue;

		int64_t begin = std::max<int64_t>( 0, input.begin );
		int64_t end = std::max( input.end, begin + options.minimumDuration );

		if ( !mergedSource.empty() )
		{
			auto& last = mergedSource.back();
			if ( getComparisonText( last.text ) == getComparisonText( input.text ) )
			{
				last.end = std::max( last.end, end );
				continue;
			}
		}
		mergedSource.push_back( { begin, end, input.text } );
	}

	for( const SourceSegment& input : mergedSource )
	{
		const std::string normalized = normalizeWhitespace( input.text );
		if( normalized.empty() )
			continue;
		const auto chunks = splitChunks( normalized, maxCueCharacters );
		if( chunks.empty() )
			continue;

		int64_t sourceBegin = input.begin;
		int64_t sourceEnd = input.end;
		const int64_t sourceDuration = sourceEnd - sourceBegin;
		size_t totalCharacters = 0;
		for( const std::string& chunk : chunks )
			totalCharacters += std::max<size_t>( 1, codePointCount( chunk ) );

		size_t consumedCharacters = 0;
		for( const std::string& chunk : chunks )
		{
			const size_t characters = std::max<size_t>( 1, codePointCount( chunk ) );
			int64_t begin = sourceBegin + sourceDuration * consumedCharacters / totalCharacters;
			consumedCharacters += characters;
			int64_t end = sourceBegin + sourceDuration * consumedCharacters / totalCharacters;
			begin = std::max( begin, previousEnd + options.minimumGap );
			end = std::max( end, begin + options.minimumDuration );
			if( options.maximumDuration > 0 )
				end = std::min( end, begin + options.maximumDuration );
			result.push_back( { begin, end, wrapLines( chunk, options.maxCharactersPerLine ) } );
			previousEnd = end;
		}
	}
	return result;
}

std::string Subtitle::renderText( const std::vector<Cue>& cues, bool timestamps )
{
	std::string result;
	for( const Cue& cue : cues )
	{
		if( timestamps )
			result += timestamp( cue.begin, false, true ) + " --> " + timestamp( cue.end, false, true ) + "  ";
		result += cue.text;
		result += "\r\n";
	}
	return result;
}

std::string Subtitle::renderSubRip( const std::vector<Cue>& cues )
{
	std::string result;
	for( size_t i = 0; i < cues.size(); i++ )
	{
		const Cue& cue = cues[ i ];
		result += std::to_string( i + 1 ) + "\r\n";
		result += timestamp( cue.begin, true, false ) + " --> " + timestamp( cue.end, true, false ) + "\r\n";
		result += cue.text + "\r\n\r\n";
	}
	return result;
}

std::string Subtitle::renderWebVtt( const std::vector<Cue>& cues )
{
	std::string result = "WEBVTT\r\n\r\n";
	for( const Cue& cue : cues )
	{
		result += timestamp( cue.begin, false, false ) + " --> " + timestamp( cue.end, false, false ) + "\r\n";
		result += cue.text + "\r\n\r\n";
	}
	return result;
}
