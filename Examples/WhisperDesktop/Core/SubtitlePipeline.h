#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace Subtitle
{
	struct SourceSegment
	{
		int64_t begin = 0;
		int64_t end = 0;
		std::string text;
	};

	struct Cue
	{
		int64_t begin = 0;
		int64_t end = 0;
		std::string text;
	};

	struct Options
	{
		uint32_t maxCharactersPerLine = 20;
		uint32_t maxLines = 2;
		int64_t minimumDuration = 800'0000;
		int64_t maximumDuration = 7000'0000;
		int64_t minimumGap = 20'0000;
	};

	std::vector<Cue> build( const std::vector<SourceSegment>& source, const Options& options = {} );
	std::string renderText( const std::vector<Cue>& cues, bool timestamps );
	std::string renderSubRip( const std::vector<Cue>& cues );
	std::string renderWebVtt( const std::vector<Cue>& cues );
}
