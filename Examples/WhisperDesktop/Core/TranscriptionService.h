#pragma once

#include "SubtitlePipeline.h"
#include <whisperWindows.h>
#include <atlbase.h>
#include <atlstr.h>

class ITranscriptionEvents
{
public:
	virtual ~ITranscriptionEvents() = default;
	virtual HRESULT onProgress( double value ) noexcept = 0;
	virtual HRESULT onNewSegments( Whisper::iTranscribeResult* result, uint32_t count ) noexcept = 0;
	virtual bool shouldContinue() const noexcept = 0;
};

class TranscriptionService
{
public:
	struct Request
	{
		CString mediaPath;
		uint32_t language = 0;
		bool translate = false;
	};

	struct Result
	{
		std::vector<Subtitle::SourceSegment> segments;
		int64_t mediaDuration = 0;
	};

	TranscriptionService( Whisper::iMediaFoundation* mediaFoundation, Whisper::iModel* model );
	HRESULT run( const Request& request, ITranscriptionEvents& events, Result& result );

private:
	CComPtr<Whisper::iMediaFoundation> mediaFoundation;
	CComPtr<Whisper::iModel> model;
	ITranscriptionEvents* activeEvents = nullptr;

	static HRESULT __cdecl newSegmentCallback( Whisper::iContext* context, uint32_t count, void* userData ) noexcept;
	static HRESULT __cdecl encoderBeginCallback( Whisper::iContext* context, void* userData ) noexcept;
	static HRESULT __stdcall progressCallback( double value, Whisper::iContext* context, void* userData ) noexcept;
};
