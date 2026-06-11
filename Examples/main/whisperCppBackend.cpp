#include "whisperCppBackend.h"
#include <cstdio>

template<typename T>
bool WdBackend::resolve( T& fn, const char* name )
{
	fn = reinterpret_cast<T>( GetProcAddress( hModule, name ) );
	if( !fn )
	{
		char buf[ 256 ];
		snprintf( buf, sizeof( buf ), "Failed to resolve export '%s' (GetLastError=%lu)", name, GetLastError() );
		errorMessage = buf;
		return false;
	}
	return true;
}

WdBackend::WdBackend( const wchar_t* dllName )
{
	// Build the full DLL filename (e.g. "WhisperCppBackendCuda.dll")
	std::wstring fullName = dllName;
	if( fullName.size() < 4 || fullName.substr( fullName.size() - 4 ) != L".dll" )
		fullName += L".dll";

	hModule = LoadLibraryW( fullName.c_str() );
	if( !hModule )
	{
		DWORD err = GetLastError();
		char buf[ 512 ];
		snprintf( buf, sizeof( buf ), "Unable to load '%ls' (error %lu). "
			"Make sure the DLL is in the same directory as the executable.", fullName.c_str(), err );
		errorMessage = buf;
		return;
	}

	// Resolve all function pointers
	if( !resolve( fnLoadModel, "wd_load_model" ) ) return;
	if( !resolve( fnModelReady, "wd_model_ready" ) ) return;
	if( !resolve( fnLastError, "wd_last_error" ) ) return;
	if( !resolve( fnTranscribe, "wd_transcribe" ) ) return;
	if( !resolve( fnSegmentCount, "wd_segment_count" ) ) return;
	if( !resolve( fnSegmentBegin, "wd_segment_begin" ) ) return;
	if( !resolve( fnSegmentEnd, "wd_segment_end" ) ) return;
	if( !resolve( fnSegmentText, "wd_segment_text" ) ) return;
	if( !resolve( fnFreeModel, "wd_free_model" ) ) return;
}

WdBackend::~WdBackend()
{
	if( hModule )
		FreeLibrary( hModule );
}

WdBackend::Model* WdBackend::loadModel( const wchar_t* path )
{
	return fnLoadModel ? fnLoadModel( path ) : nullptr;
}

bool WdBackend::modelReady( const Model* model )
{
	return fnModelReady && fnModelReady( model ) != 0;
}

const char* WdBackend::lastError( const Model* model )
{
	return fnLastError ? fnLastError( model ) : "Backend not loaded";
}

int WdBackend::transcribe(
	Model* model,
	const float* samples,
	int sampleCount,
	const char* language,
	int translate,
	ProgressCallback progress,
	SegmentCallback segment,
	CancelCallback cancel,
	void* userData )
{
	if( !fnTranscribe )
		return -1;
	return fnTranscribe( model, samples, sampleCount, language, translate,
		progress, segment, cancel, userData );
}

int WdBackend::segmentCount( const Model* model )
{
	return fnSegmentCount ? fnSegmentCount( model ) : 0;
}

int64_t WdBackend::segmentBegin( const Model* model, int index )
{
	return fnSegmentBegin ? fnSegmentBegin( model, index ) : 0;
}

int64_t WdBackend::segmentEnd( const Model* model, int index )
{
	return fnSegmentEnd ? fnSegmentEnd( model, index ) : 0;
}

const char* WdBackend::segmentText( const Model* model, int index )
{
	return fnSegmentText ? fnSegmentText( model, index ) : "";
}

void WdBackend::freeModel( Model* model )
{
	if( fnFreeModel )
		fnFreeModel( model );
}
