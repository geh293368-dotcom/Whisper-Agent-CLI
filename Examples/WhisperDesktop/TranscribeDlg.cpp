#include "stdafx.h"
#include "TranscribeDlg.h"
#include "Utils/logger.h"

HRESULT TranscribeDlg::show()
{
	auto res = DoModal( nullptr );
	if( res == -1 )
		return HRESULT_FROM_WIN32( GetLastError() );
	switch( res )
	{
	case IDC_BACK:
		return SCREEN_MODEL;
	case IDC_CAPTURE:
		return SCREEN_CAPTURE;
	}
	return S_OK;
}

constexpr int progressMaxInteger = 1024 * 8;

static const LPCTSTR regValInput = L"\u6E90\u5A92\u4F53";
static const LPCTSTR regValOutFormat = L"\u7ED3\u679C\u683C\u5F0F";
static const LPCTSTR regValOutPath = L"\u7ED3\u679C\u8DEF\u5F84";
static const LPCTSTR regValUseInputFolder = L"\u4F7F\u7528\u8F93\u5165\u6587\u4EF6\u5939";
static const std::array<LPCTSTR, 4> outputExtensions =
{
	L".txt", L".txt", L".srt", L".vtt"
};

LRESULT TranscribeDlg::OnInitDialog( UINT nMessage, WPARAM wParam, LPARAM lParam, BOOL& bHandled )
{
	// First DDX call, hooks up variables to controls.
	DoDataExchange( false );
	printModelDescription();
	languageSelector.initialize( m_hWnd, IDC_LANGUAGE, appState );
	cbConsole.initialize( m_hWnd, IDC_CONSOLE, appState );
	cbTranslate.initialize( m_hWnd, IDC_TRANSLATE, appState );
	populateOutputFormats();

	pendingState.initialize(
		{
			languageSelector, GetDlgItem( IDC_TRANSLATE ),
			fileList, GetDlgItem( IDC_ADD_FILES ),
			GetDlgItem( IDC_REMOVE_FILE ), GetDlgItem( IDC_CLEAR_FILES ),
			transcribeOutFormat, useInputFolder,
			transcribeOutputPath, GetDlgItem( IDC_BROWSE_RESULT ),
			GetDlgItem( IDCANCEL ),
			GetDlgItem( IDC_BACK ),
			GetDlgItem( IDC_CAPTURE )
		},
		{
			progressBar, GetDlgItem( IDC_PENDING_TEXT )
		} );

	HRESULT hr = work.create( this );
	if( FAILED( hr ) )
	{
       reportError( m_hWnd, L"\u521B\u5EFA\u7EBF\u7A0B\u6C60\u5DE5\u4F5C\u5931\u8D25", nullptr, hr );
		EndDialog( IDCANCEL );
	}

	progressBar.SetRange32( 0, progressMaxInteger );
	progressBar.SetStep( 1 );

	lastInputPath = appState.stringLoad( regValInput );
	transcribeOutFormat.SetCurSel( (int)appState.dwordLoad( regValOutFormat, 0 ) );
	transcribeOutputPath.SetWindowText( appState.stringLoad( regValOutPath ) );
	if( appState.boolLoad( regValUseInputFolder ) )
		useInputFolder.SetCheck( BST_CHECKED );
	BOOL unused;
	onOutFormatChange( 0, 0, nullptr, unused );
	updateQueueButtons();

	appState.lastScreenSave( SCREEN_TRANSCRIBE );
	appState.setupIcon( this );
	ATLVERIFY( CenterWindow() );
	return 0;
}

void TranscribeDlg::printModelDescription()
{
	CString text;
	if( S_OK == appState.model->isMultilingual() )
     text = L"\u591A\u79CD\u8BED\u8A00";
	else
      text = L"\u5355\u8BED\u8A00";
	text += L" \u6A21\u578B \"";
	LPCTSTR path = appState.source.path;
	path = ::PathFindFileName( path );
	text += path;
	text += L"\", ";
	const int64_t cb = appState.source.sizeInBytes;
	if( cb < 1 << 30 )
	{
		constexpr double mul = 1.0 / ( 1 << 20 );
		double mb = (double)cb * mul;
		text.AppendFormat( L"%.1f MB", mb );
	}
	else
	{
		constexpr double mul = 1.0 / ( 1 << 30 );
		double gb = (double)cb * mul;
		text.AppendFormat( L"%.2f GB", gb );
	}
 text += L" \u5728\u78C1\u76D8\u4E0A, ";
	text += implString( appState.source.impl );
 text += L" \u6267\u884C";

	modelDesc.SetWindowText( text );
}

// Populate the "Output Format" combobox
void TranscribeDlg::populateOutputFormats()
{
    transcribeOutFormat.AddString( L"\u4E0D\u8F93\u51FA" );
	transcribeOutFormat.AddString( L"\u6587\u672C\u6587\u4EF6\uFF08.txt\uFF09" );
	transcribeOutFormat.AddString( L"\u5E26\u65F6\u95F4\u6233\u7684\u6587\u672C\uFF08.txt\uFF09" );
	transcribeOutFormat.AddString( L"SubRip \u5B57\u5E55\uFF08.srt\uFF09" );
	transcribeOutFormat.AddString( L"WebVTT \u5B57\u5E55\uFF08.vtt\uFF09" );
}

// CBN_SELCHANGE notification for IDC_OUTPUT_FORMAT combobox
LRESULT TranscribeDlg::onOutFormatChange( UINT, INT, HWND, BOOL& bHandled )
{
	BOOL enabled = transcribeOutFormat.GetCurSel() != 0;
	useInputFolder.EnableWindow( enabled );
	const BOOL allowFolder = ( enabled && !isChecked( useInputFolder ) ) ? TRUE : FALSE;
	transcribeOutputPath.EnableWindow( allowFolder );
	transcribeOutputBrowse.EnableWindow( allowFolder );

	return 0;
}

void TranscribeDlg::onAddFiles()
{
	std::vector<CString> paths;
	CString initialDir = lastInputPath;
	if( initialDir.GetLength() > 0 )
	{
		wchar_t* buf = initialDir.GetBuffer();
		if( !PathRemoveFileSpec( buf ) )
			initialDir.Empty();
		initialDir.ReleaseBuffer();
	}
	LPCTSTR seed = initialDir.GetLength() > 0 ? (LPCTSTR)initialDir : nullptr;
	if( !pickAudioFiles( m_hWnd, paths, seed ) )
		return;

	for( const CString& p : paths )
		queue.add( p );
	lastInputPath = paths.front();
	refreshQueueDisplay();
	updateQueueButtons();
}

void TranscribeDlg::onRemoveFiles()
{
	const int selected = fileList.GetSelCount();
	if( selected <= 0 )
		return;
	std::vector<int> indices( selected );
	if( fileList.GetSelItems( selected, indices.data() ) == LB_ERR )
		return;
	queue.remove( indices );
	refreshQueueDisplay();
	updateQueueButtons();
}

void TranscribeDlg::onClearFiles()
{
	queue.clear();
	refreshQueueDisplay();
	updateQueueButtons();
}

LRESULT TranscribeDlg::onQueueSelectionChanged( UINT, INT, HWND, BOOL& )
{
	updateQueueButtons();
	return 0;
}

CString TranscribeDlg::formatStatus( TranscriptionQueue::State state, HRESULT hr )
{
	using State = TranscriptionQueue::State;
	switch( state )
	{
	case State::Pending:
      return L"\u5F85\u529E\u7684";
	case State::Running:
      return L"\u6267\u884C\u4E2D";
	case State::Completed:
       return L"\u5B8C\u6210";
	case State::Failed:
	{
		CString txt;
       txt.Format( L"\u5931\u8D25 (0x%08X)", hr );
		return txt;
	}
	default:
		return L"";
	}
}

void TranscribeDlg::refreshQueueDisplay()
{
	fileList.ResetContent();
	for( const TranscriptionQueue::Item& item : queue.items() )
	{
		CString text = formatStatus( item.state, item.result );
		text += L" - ";
		LPCTSTR name = PathFindFileName( item.inputPath );
		text += name;
		fileList.AddString( text );
	}
}

void TranscribeDlg::updateQueueButtons()
{
	const BOOL hasItems = queue.empty() ? FALSE : TRUE;
	const int selection = fileList.GetSelCount();
	const BOOL canEdit = ( transcribeArgs.visualState == eVisualState::Idle ) ? TRUE : FALSE;
	CWindow addBtn = GetDlgItem( IDC_ADD_FILES );
	CWindow removeBtn = GetDlgItem( IDC_REMOVE_FILE );
	CWindow clearBtn = GetDlgItem( IDC_CLEAR_FILES );
	if( addBtn )
		addBtn.EnableWindow( canEdit );
	if( removeBtn )
		removeBtn.EnableWindow( canEdit && selection > 0 );
	if( clearBtn )
		clearBtn.EnableWindow( canEdit && hasItems );
}

bool TranscribeDlg::ensureOutputFolder( CString& folder )
{
	folder.Trim();
	if( folder.IsEmpty() )
	{
        transcribeError( L"\u8BF7\u9009\u62E9\u8F93\u51FA\u6587\u4EF6\u5939." );
		return false;
	}
	DWORD attrs = GetFileAttributes( folder );
	if( attrs == INVALID_FILE_ATTRIBUTES || ( attrs & FILE_ATTRIBUTE_DIRECTORY ) == 0 )
	{
       CString msg = L"\u8F93\u51FA\u6587\u4EF6\u5939\u4E0D\u5B58\u5728:\n";
		msg += folder;
		transcribeError( msg );
		return false;
	}
	return true;
}

CString TranscribeDlg::composeOutputPath( const CString& input, const CString& explicitFolder ) const
{
	CString folder = explicitFolder;
	if( folder.IsEmpty() )
	{
		folder = input;
		wchar_t* buf = folder.GetBuffer();
		PathRemoveFileSpec( buf );
		folder.ReleaseBuffer();
	}

	CString fileName = PathFindFileName( input );
	CString baseName = fileName;
	wchar_t* base = baseName.GetBuffer();
	PathRemoveExtension( base );
	baseName.ReleaseBuffer();
	const int formatIndex = (int)transcribeArgs.format - 1;
	LPCTSTR ext = ( formatIndex >= 0 && formatIndex < (int)outputExtensions.size() ) ? outputExtensions[ formatIndex ] : L".txt";
	CString targetName = baseName;
	targetName += ext;

	wchar_t combined[ MAX_PATH ] = {};
	CString result;
	if( PathCombine( combined, folder, targetName ) != nullptr )
		result = combined;
	else
	{
		result = folder;
		if( !result.IsEmpty() && result[ result.GetLength() - 1 ] != L'\\' )
			result += L'\\';
		result += targetName;
	}
	return result;
}

bool TranscribeDlg::prepareBatchItems( const CString& explicitFolder )
{
	if( queue.empty() )
	{
       transcribeError( L"\u8BF7\u81F3\u5C11\u6DFB\u52A0\u4E00\u4E2A\u97F3\u9891\u6587\u4EF6\u3002" );
		return false;
	}
	for( const TranscriptionQueue::Item& item : queue.items() )
	{
		if( PathFileExists( item.inputPath ) )
			continue;
      CString msg = L"\u8F93\u5165\u97F3\u9891\u6587\u4EF6\u4E0D\u5B58\u5728:\n";
		msg += item.inputPath;
		transcribeError( msg, HRESULT_FROM_WIN32( ERROR_FILE_NOT_FOUND ) );
		return false;
	}

	bool needOverwritePrompt = false;
	queue.reset();
	for( size_t i = 0; i < queue.size(); i++ )
	{
		TranscriptionQueue::Item& item = queue[ i ];
		if( transcribeArgs.format == eOutputFormat::None )
		{
			item.outputPath.Empty();
			continue;
		}
		item.outputPath = composeOutputPath( item.inputPath, explicitFolder );
		if( PathFileExists( item.outputPath ) )
			needOverwritePrompt = true;
	}

	if( needOverwritePrompt )
	{
      const int resp = MessageBox( L"\u90E8\u5206\u8F93\u51FA\u6587\u4EF6\u5DF2\u5B58\u5728\u3002\n\u8981\u5168\u90E8\u8986\u76D6\u5417?", L"\u786E\u8BA4\u8986\u76D6", MB_ICONQUESTION | MB_YESNO );
		if( resp != IDYES )
			return false;
	}

	refreshQueueDisplay();
	return true;
}

bool TranscribeDlg::startNextItem()
{
	if( transcribeArgs.visualState != eVisualState::Running )
		return false;
	const int next = queue.startNext();
	if( next >= 0 )
	{
		TranscriptionQueue::Item& item = queue[ next ];
		transcribeArgs.pathMedia = item.inputPath;
		transcribeArgs.pathOutput = item.outputPath;
		progressBar.SetPos( 0 );
		refreshQueueDisplay();
		HRESULT hr = work.post();
		if( FAILED( hr ) )
		{
			finalizeCurrentItem( hr );
          transcribeError( L"\u65E0\u6CD5\u5C06\u8F6C\u5F55\u4EFB\u52A1\u52A0\u5165\u961F\u5217", hr );
			return false;
		}
		return true;
	}
	return false;
}

void TranscribeDlg::finalizeCurrentItem( HRESULT hr )
{
	const int runningItem = queue.runningIndex();
	if( runningItem < 0 || runningItem >= (int)queue.size() )
		return;
	queue.completeCurrent( hr );
	refreshQueueDisplay();
	if( FAILED( hr ) )
	{
     CString msg = L"\u8F6C\u5F55\u5931\u8D25";
		if( transcribeArgs.errorMessage.GetLength() > 0 )
		{
			msg += L"\n";
			msg += transcribeArgs.errorMessage;
		}
		transcribeError( msg, hr );
	}
}

void TranscribeDlg::finishBatch( bool canceled )
{
	transcribeArgs.visualState = eVisualState::Idle;
	setPending( false );
    transcribeButton.SetWindowText( L"\u8F6C\u5F55" );
	transcribeButton.EnableWindow( TRUE );
	progressBar.SetPos( 0 );
	updateQueueButtons();

	size_t completed = 0;
	size_t failed = 0;
	size_t pending = 0;
	using State = TranscriptionQueue::State;
	for( const TranscriptionQueue::Item& item : queue.items() )
	{
		switch( item.state )
		{
		case State::Completed:
			completed++;
			break;
		case State::Failed:
			failed++;
			break;
		case State::Pending:
			pending++;
			break;
		default:
			break;
		}
	}
	CString summary;
	if( canceled )
     summary = L"\u6279\u91CF\u4EFB\u52A1\u5728\u5904\u7406\u5B8C\u6240\u6709\u6587\u4EF6\u524D\u5DF2\u505C\u6B62\u3002";
	else
      summary = L"\u6279\u91CF\u8F6C\u5F55\u5DF2\u5B8C\u6210\u3002";
	summary.AppendFormat( L"\n\u5B8C\u6210: %zu\n\u5931\u8D25: %zu", completed, failed );
	if( pending > 0 )
        summary.AppendFormat( L"\n\u5F85\u5904\u7406: %zu", pending );
	MessageBox( summary, L"\u8F6C\u5F55", MB_OK | MB_ICONINFORMATION );
}


void TranscribeDlg::onInputFolderCheck()
{
	const BOOL allowFolder = ( transcribeOutFormat.GetCurSel() != 0 && !isChecked( useInputFolder ) ) ? TRUE : FALSE;
	transcribeOutputPath.EnableWindow( allowFolder );
	transcribeOutputBrowse.EnableWindow( allowFolder );
}

void TranscribeDlg::onBrowseOutput()
{
	CString folder;
	transcribeOutputPath.GetWindowText( folder );
	if( !browseForFolder( m_hWnd, folder ) )
		return;
	transcribeOutputPath.SetWindowText( folder );
}

void TranscribeDlg::setPending( bool nowPending )
{
	pendingState.setPending( nowPending );
}

void TranscribeDlg::transcribeError( LPCTSTR text, HRESULT hr )
{
    reportError( m_hWnd, text, L"\u65E0\u6CD5\u8F6C\u5F55\u97F3\u9891", hr );
}

void TranscribeDlg::onTranscribe()
{
	switch( transcribeArgs.visualState )
	{
	case eVisualState::Running:
		transcribeArgs.visualState = eVisualState::Stopping;
		transcribeButton.EnableWindow( FALSE );
		return;
	case eVisualState::Stopping:
		return;
	}

	transcribeArgs.language = languageSelector.selectedLanguage();
	transcribeArgs.translate = cbTranslate.checked();
	if( isInvalidTranslate( m_hWnd, transcribeArgs.language, transcribeArgs.translate ) )
		return;

	transcribeArgs.format = (eOutputFormat)(uint8_t)transcribeOutFormat.GetCurSel();
	CString explicitFolder;
	if( transcribeArgs.format == eOutputFormat::None )
		cbConsole.ensureChecked();
	else if( !isChecked( useInputFolder ) )
	{
		transcribeOutputPath.GetWindowText( explicitFolder );
		if( !ensureOutputFolder( explicitFolder ) )
			return;
	}

	appState.dwordStore( regValOutFormat, (uint32_t)(int)transcribeArgs.format );
	appState.boolStore( regValUseInputFolder, isChecked( useInputFolder ) );
	languageSelector.saveSelection( appState );
	cbTranslate.saveSelection( appState );
	if( !explicitFolder.IsEmpty() )
		appState.stringStore( regValOutPath, explicitFolder );
	if( !queue.empty() )
		appState.stringStore( regValInput, queue[ 0 ].inputPath );

	if( !prepareBatchItems( explicitFolder ) )
		return;

	setPending( true );
	transcribeArgs.visualState = eVisualState::Running;
  transcribeButton.SetWindowText( L"\u505C\u6B62" );
	updateQueueButtons();
	if( !startNextItem() )
		finishBatch( true );
}

void __stdcall TranscribeDlg::poolCallback() noexcept
{
	HRESULT hr = transcribe();
	PostMessage( WM_CALLBACK_STATUS, (WPARAM)hr );
}

LRESULT TranscribeDlg::onCallbackStatus( UINT, WPARAM wParam, LPARAM, BOOL& )
{
	const HRESULT hr = (HRESULT)wParam;
	finalizeCurrentItem( hr );

	if( transcribeArgs.visualState == eVisualState::Stopping )
	{
		finishBatch( true );
		return 0;
	}

	if( startNextItem() )
		return 0;

	finishBatch( false );
	return 0;
}

void TranscribeDlg::getThreadError()
{
	getLastError( transcribeArgs.errorMessage );
}

#define CHECK_EX( hr ) { const HRESULT __hr = ( hr ); if( FAILED( __hr ) ) { getThreadError(); return __hr; } }

HRESULT TranscribeDlg::transcribe()
{
	clearLastError();
	transcribeArgs.errorMessage = L"";

	const eOutputFormat format = transcribeArgs.format;
	TranscriptionService service( appState.mediaFoundation, appState.model );
	TranscriptionService::Request request{ transcribeArgs.pathMedia, transcribeArgs.language, transcribeArgs.translate };
	TranscriptionService::Result result;
	CHECK_EX( service.run( request, *this, result ) );

	if( format == eOutputFormat::None )
		return S_OK;

	const std::vector<Subtitle::Cue> cues = Subtitle::build( result.segments );
	std::string content;

	switch( format )
	{
	case eOutputFormat::Text:
		content = Subtitle::renderText( cues, false );
		break;
	case eOutputFormat::TextTimestamps:
		content = Subtitle::renderText( cues, true );
		break;
	case eOutputFormat::SubRip:
		content = Subtitle::renderSubRip( cues );
		break;
	case eOutputFormat::WebVTT:
		content = Subtitle::renderWebVtt( cues );
		break;
	default:
		return E_FAIL;
	}

	CAtlFile outputFile;
	CHECK_EX( outputFile.Create( transcribeArgs.pathOutput, GENERIC_WRITE, 0, CREATE_ALWAYS ) );
	CHECK_EX( writeUtf8Bom( outputFile ) );
	if( !content.empty() )
		CHECK_EX( outputFile.Write( content.data(), (DWORD)content.size() ) );
	return S_OK;
}

#undef CHECK_EX

HRESULT TranscribeDlg::onProgress( double value ) noexcept
{
	constexpr double mul = progressMaxInteger;
	int pos = lround( mul * value );
	progressBar.PostMessage( PBM_SETPOS, pos, 0 );
	return S_OK;
}

HRESULT TranscribeDlg::onNewSegments( Whisper::iTranscribeResult* result, uint32_t count ) noexcept
{
	return logNewSegments( result, count );
}

bool TranscribeDlg::shouldContinue() const noexcept
{
	switch( transcribeArgs.visualState )
	{
	case eVisualState::Running:
		return true;
	case eVisualState::Idle:
	case eVisualState::Stopping:
	default:
		return false;
	}
}

void TranscribeDlg::onWmClose()
{
	if( GetDlgItem( IDCANCEL ).IsWindowEnabled() )
	{
		EndDialog( IDCANCEL );
		return;
	}

	constexpr UINT flags = MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2;
    const int res = this->MessageBox( L"\u6B63\u5728\u8F6C\u5F55\u4E2D\u3002\n\u4ECD\u7136\u8981\u9000\u51FA\u5417?", L"\u786E\u8BA4\u9000\u51FA", flags );
	if( res != IDYES )
		return;

	// TODO: instead of ExitProcess(), implement another callback in the DLL API, for proper cancellation of the background task
	ExitProcess( 1 );
}
