#pragma once
#include <cstdint>
#include <string>
#include <windows.h>

// Dynamically loads WhisperCppBackendCuda.dll or WhisperCppBackendCpu.dll
// and exposes the wd_* API through type-safe C++ methods.
class WdBackend
{
public:
	// dllName: "WhisperCppBackendCuda" or "WhisperCppBackendCpu" (without .dll)
	explicit WdBackend( const wchar_t* dllName );
	~WdBackend();

	WdBackend( const WdBackend& ) = delete;
	WdBackend& operator=( const WdBackend& ) = delete;

	bool loaded() const { return hModule != nullptr; }
	const std::string& loadError() const { return errorMessage; }

	// Callback types matching the backend DLL exports
	using ProgressCallback = void( __cdecl* )( int progress, void* userData );
	using SegmentCallback = void( __cdecl* )( int64_t begin, int64_t end, const char* text, void* userData );
	using CancelCallback = int( __cdecl* )( void* userData );

	// Opaque model handle from the backend
	struct Model;

	Model* loadModel( const wchar_t* path );
	bool modelReady( const Model* model );
	const char* lastError( const Model* model );

	int transcribe(
		Model* model,
		const float* samples,
		int sampleCount,
		const char* language,
		int translate,
		ProgressCallback progress,
		SegmentCallback segment,
		CancelCallback cancel,
		void* userData );

	int segmentCount( const Model* model );
	int64_t segmentBegin( const Model* model, int index );
	int64_t segmentEnd( const Model* model, int index );
	const char* segmentText( const Model* model, int index );
	void freeModel( Model* model );

private:
	HMODULE hModule = nullptr;
	std::string errorMessage;

	// Function pointer types
	using FnLoadModel = Model* ( __cdecl* )( const wchar_t* path );
	using FnModelReady = int( __cdecl* )( const Model* model );
	using FnLastError = const char* ( __cdecl* )( const Model* model );
	using FnTranscribe = int( __cdecl* )(
		Model* model, const float* samples, int sampleCount,
		const char* language, int translate,
		ProgressCallback progress, SegmentCallback segment,
		CancelCallback cancel, void* userData );
	using FnSegmentCount = int( __cdecl* )( const Model* model );
	using FnSegmentTime = int64_t( __cdecl* )( const Model* model, int index );
	using FnSegmentText = const char* ( __cdecl* )( const Model* model, int index );
	using FnFreeModel = void( __cdecl* )( Model* model );

	FnLoadModel fnLoadModel = nullptr;
	FnModelReady fnModelReady = nullptr;
	FnLastError fnLastError = nullptr;
	FnTranscribe fnTranscribe = nullptr;
	FnSegmentCount fnSegmentCount = nullptr;
	FnSegmentTime fnSegmentBegin = nullptr;
	FnSegmentTime fnSegmentEnd = nullptr;
	FnSegmentText fnSegmentText = nullptr;
	FnFreeModel fnFreeModel = nullptr;

	template<typename T>
	bool resolve( T& fn, const char* name );
};
