#pragma once

namespace Whisper
{
	inline bool shouldSkipSilentWindow( bool hasTextToken, int seekDelta, int chunkSize ) noexcept
	{
		return !hasTextToken && seekDelta < 100 * chunkSize / 2;
	}
}
