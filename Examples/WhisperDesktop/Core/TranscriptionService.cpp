#include "stdafx.h"
#include "TranscriptionService.h"

TranscriptionService::TranscriptionService( Whisper::iMediaFoundation* mediaFoundation, Whisper::iModel* model )
	: mediaFoundation( mediaFoundation ), model( model )
{
}

HRESULT TranscriptionService::run( const Request& request, ITranscriptionEvents& events, Result& result )
{
	if( mediaFoundation == nullptr || model == nullptr )
		return E_POINTER;
	result = {};
	activeEvents = &events;

	using namespace Whisper;
	CComPtr<iAudioReader> reader;
	HRESULT hr = mediaFoundation->openAudioFile( request.mediaPath, false, &reader );
	if( FAILED( hr ) )
		return hr;

	CComPtr<iContext> context;
	hr = model->createContext( &context );
	if( FAILED( hr ) )
		return hr;

	sFullParams params;
	hr = context->fullDefaultParams( eSamplingStrategy::Greedy, &params );
	if( FAILED( hr ) )
		return hr;
	params.language = request.language;
	params.setFlag( eFullParamsFlags::Translate, request.translate );
	params.resetFlag( eFullParamsFlags::PrintRealtime );
	params.new_segment_callback = &newSegmentCallback;
	params.new_segment_callback_user_data = this;
	params.encoder_begin_callback = &encoderBeginCallback;
	params.encoder_begin_callback_user_data = this;

	sProgressSink progress{ &progressCallback, this };
	hr = context->runStreamed( params, progress, reader );
	activeEvents = nullptr;
	if( FAILED( hr ) )
		return hr;

	hr = reader->getDuration( result.mediaDuration );
	if( FAILED( hr ) )
		return hr;
	context->timingsPrint();

	CComPtr<iTranscribeResult> nativeResult;
	hr = context->getResults( eResultFlags::Timestamps | eResultFlags::Tokens, &nativeResult );
	if( FAILED( hr ) )
		return hr;
	sTranscribeLength length;
	hr = nativeResult->getSize( length );
	if( FAILED( hr ) )
		return hr;

	const sSegment* segments = nativeResult->getSegments();
	result.segments.reserve( length.countSegments );
	for( uint32_t i = 0; i < length.countSegments; i++ )
	{
		const sSegment& segment = segments[ i ];
		result.segments.push_back( { (int64_t)segment.time.begin.ticks, (int64_t)segment.time.end.ticks,
			segment.text != nullptr ? segment.text : "" } );
	}
	return S_OK;
}

HRESULT __cdecl TranscriptionService::newSegmentCallback( Whisper::iContext* context, uint32_t count, void* userData ) noexcept
{
	auto& service = *(TranscriptionService*)userData;
	if( service.activeEvents == nullptr )
		return S_OK;
	CComPtr<Whisper::iTranscribeResult> result;
	HRESULT hr = context->getResults( Whisper::eResultFlags::Timestamps | Whisper::eResultFlags::Tokens, &result );
	if( FAILED( hr ) )
		return hr;
	return service.activeEvents->onNewSegments( result, count );
}

HRESULT __cdecl TranscriptionService::encoderBeginCallback( Whisper::iContext*, void* userData ) noexcept
{
	auto& service = *(TranscriptionService*)userData;
	return service.activeEvents == nullptr || service.activeEvents->shouldContinue() ? S_OK : S_FALSE;
}

HRESULT __stdcall TranscriptionService::progressCallback( double value, Whisper::iContext*, void* userData ) noexcept
{
	auto& service = *(TranscriptionService*)userData;
	return service.activeEvents != nullptr ? service.activeEvents->onProgress( value ) : S_OK;
}
