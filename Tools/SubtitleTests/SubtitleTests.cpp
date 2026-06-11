#include "../../Examples/WhisperDesktop/Core/SubtitlePipeline.h"
#include "../../Examples/WhisperDesktop/Core/TranscriptionQueue.h"
#include "../../Whisper/Whisper/SilenceRecovery.h"

#include <iostream>
#include <stdexcept>
#include <string>

namespace
{
	void require( bool condition, const char* message )
	{
		if( !condition )
			throw std::runtime_error( message );
	}

	void cleanupAndTimelineTest()
	{
		const std::vector<Subtitle::SourceSegment> source =
		{
			{ 0, 200'0000, "   first   line  " },
			{ 100'0000, 150'0000, "\tsecond line" },
			{ 300'0000, 400'0000, "   " },
		};
		const auto cues = Subtitle::build( source );
		require( cues.size() == 2, "blank segments must be removed" );
		require( cues[ 0 ].text == "first line", "whitespace must be normalized" );
		require( cues[ 1 ].begin >= cues[ 0 ].end + 20'0000, "cue timestamps must not overlap" );
		require( cues[ 1 ].end - cues[ 1 ].begin >= 800'0000, "short cues need a readable duration" );
	}

	void longChineseSubtitleTest()
	{
		Subtitle::Options options;
		options.maxCharactersPerLine = 8;
		options.maxLines = 2;
		const std::vector<Subtitle::SourceSegment> source =
		{
			{ 0, 12000'0000, "这是一个很长的中文字幕，用来验证标点断句和自动换行不会破坏UTF8字符。" },
		};
		const auto cues = Subtitle::build( source, options );
		require( cues.size() >= 2, "long subtitles must be split into multiple cues" );
		for( const auto& cue : cues )
			require( !cue.text.empty(), "split cues must contain text" );
	}

	void formatterTest()
	{
		const std::vector<Subtitle::Cue> cues = { { 0, 150'0000, "hello" } };
		const std::string srt = Subtitle::renderSubRip( cues );
		const std::string vtt = Subtitle::renderWebVtt( cues );
		require( srt.find( "00:00:00,000 --> 00:00:00,150" ) != std::string::npos, "SRT timestamp format is invalid" );
		require( vtt.find( "WEBVTT\r\n\r\n" ) == 0, "WebVTT header is missing" );
	}

	void silenceRecoveryTest()
	{
		require( Whisper::shouldSkipSilentWindow( false, 10, 3000 ), "empty timestamp loops must skip the window" );
		require( !Whisper::shouldSkipSilentWindow( true, 10, 3000 ), "text output must not trigger silence recovery" );
		require( !Whisper::shouldSkipSilentWindow( false, 150000, 3000 ), "normal seek progress must not be overridden" );
	}

	void queueStateTest()
	{
		TranscriptionQueue queue;
		queue.add( L"one.wav" );
		queue.add( L"two.wav" );
		require( queue.startNext() == 0, "the first pending task must start first" );
		queue.completeCurrent( S_OK );
		require( queue[ 0 ].state == TranscriptionQueue::State::Completed, "successful tasks must be completed" );
		require( queue.startNext() == 1, "the next pending task must start" );
		queue.completeCurrent( E_FAIL );
		require( queue[ 1 ].state == TranscriptionQueue::State::Failed, "failed tasks must retain their result" );
		queue.reset();
		require( queue.runningIndex() == -1 && queue[ 0 ].state == TranscriptionQueue::State::Pending,
			"reset must prepare all tasks for another batch" );
	}
}

int main()
{
	try
	{
		cleanupAndTimelineTest();
		longChineseSubtitleTest();
		formatterTest();
		silenceRecoveryTest();
		queueStateTest();
		std::cout << "Subtitle regression tests passed\n";
		return 0;
	}
	catch( const std::exception& ex )
	{
		std::cerr << "Subtitle regression test failed: " << ex.what() << '\n';
		return 1;
	}
}
