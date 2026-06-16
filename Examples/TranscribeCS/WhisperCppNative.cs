using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TranscribeCS;

sealed class WhisperCppNative: IDisposable
{
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	internal delegate void ProgressCallback( int progress, IntPtr userData );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	internal delegate void SegmentCallback( long begin, long end, IntPtr text, IntPtr userData );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	internal delegate int CancelCallback( IntPtr userData );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl, CharSet = CharSet.Unicode )]
	delegate IntPtr LoadModelDelegate( [MarshalAs( UnmanagedType.LPWStr )] string path );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate int ModelReadyDelegate( IntPtr model );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate IntPtr LastErrorDelegate( IntPtr model );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate int TranscribeDelegate(
		IntPtr model,
		IntPtr samples,
		int sampleCount,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string language,
		int translate,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string? initialPrompt,
		ProgressCallback progress,
		SegmentCallback segment,
		CancelCallback cancel,
		IntPtr userData );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate int SegmentCountDelegate( IntPtr model );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate long SegmentTimeDelegate( IntPtr model, int index );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate IntPtr SegmentTextDelegate( IntPtr model, int index );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	internal delegate void FreeModelDelegate( IntPtr model );

	readonly IntPtr library;
	readonly LoadModelDelegate loadModel;
	readonly ModelReadyDelegate modelReady;
	readonly LastErrorDelegate lastError;
	readonly TranscribeDelegate transcribe;
	readonly SegmentCountDelegate segmentCount;
	readonly SegmentTimeDelegate segmentBegin;
	readonly SegmentTimeDelegate segmentEnd;
	readonly SegmentTextDelegate segmentText;
	readonly FreeModelDelegate freeModel;
	bool disposed;

	public WhisperCppNative( string libraryName )
	{
		library = NativeLibrary.Load( libraryName, Assembly.GetExecutingAssembly(), null );
		loadModel = getExport<LoadModelDelegate>( "wd_load_model" );
		modelReady = getExport<ModelReadyDelegate>( "wd_model_ready" );
		lastError = getExport<LastErrorDelegate>( "wd_last_error" );
		transcribe = getExport<TranscribeDelegate>( "wd_transcribe" );
		segmentCount = getExport<SegmentCountDelegate>( "wd_segment_count" );
		segmentBegin = getExport<SegmentTimeDelegate>( "wd_segment_begin" );
		segmentEnd = getExport<SegmentTimeDelegate>( "wd_segment_end" );
		segmentText = getExport<SegmentTextDelegate>( "wd_segment_text" );
		freeModel = getExport<FreeModelDelegate>( "wd_free_model" );
	}

	T getExport<T>( string name ) where T: Delegate =>
		Marshal.GetDelegateForFunctionPointer<T>( NativeLibrary.GetExport( library, name ) );

	public ModelHandle LoadModel( string path ) => new ModelHandle( loadModel( path ), freeModel );
	public bool ModelReady( ModelHandle model ) => modelReady( model.DangerousGetHandle() ) != 0;
	public string GetError( ModelHandle model ) =>
		Marshal.PtrToStringUTF8( lastError( model.DangerousGetHandle() ) ) ?? "whisper.cpp returned an unknown error.";

	public int Transcribe(
		ModelHandle model,
		IntPtr samples,
		int sampleCount,
		string language,
		bool translate,
		string? initialPrompt,
		ProgressCallback progress,
		SegmentCallback segment,
		CancelCallback cancel ) =>
		transcribe( model.DangerousGetHandle(), samples, sampleCount, language, translate ? 1 : 0,
			initialPrompt, progress, segment, cancel, IntPtr.Zero );

	public int SegmentCount( ModelHandle model ) => segmentCount( model.DangerousGetHandle() );
	public long SegmentBegin( ModelHandle model, int index ) => segmentBegin( model.DangerousGetHandle(), index );
	public long SegmentEnd( ModelHandle model, int index ) => segmentEnd( model.DangerousGetHandle(), index );
	public string SegmentText( ModelHandle model, int index ) =>
		Marshal.PtrToStringUTF8( segmentText( model.DangerousGetHandle(), index ) ) ?? string.Empty;

	public void Dispose()
	{
		if( disposed )
			return;
		NativeLibrary.Free( library );
		disposed = true;
	}

	internal sealed class ModelHandle: SafeHandleZeroOrMinusOneIsInvalid
	{
		readonly FreeModelDelegate freeModel;

		public ModelHandle( IntPtr handle, FreeModelDelegate freeModel ) : base( true )
		{
			this.freeModel = freeModel;
			SetHandle( handle );
		}

		protected override bool ReleaseHandle()
		{
			freeModel( handle );
			return true;
		}
	}
}
