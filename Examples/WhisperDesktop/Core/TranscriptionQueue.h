#pragma once

#include <Windows.h>
#include <atlstr.h>
#include <vector>

class TranscriptionQueue
{
public:
	enum struct State : uint8_t
	{
		Pending,
		Running,
		Completed,
		Failed,
	};

	struct Item
	{
		CString inputPath;
		CString outputPath;
		State state = State::Pending;
		HRESULT result = S_OK;
	};

	void add( const CString& inputPath );
	void remove( const std::vector<int>& indices );
	void clear();
	void reset();
	int startNext();
	void completeCurrent( HRESULT result );

	int runningIndex() const { return running; }
	bool empty() const { return entries.empty(); }
	size_t size() const { return entries.size(); }
	Item& operator[]( size_t index ) { return entries[ index ]; }
	const Item& operator[]( size_t index ) const { return entries[ index ]; }
	const std::vector<Item>& items() const { return entries; }

private:
	std::vector<Item> entries;
	int running = -1;
};
