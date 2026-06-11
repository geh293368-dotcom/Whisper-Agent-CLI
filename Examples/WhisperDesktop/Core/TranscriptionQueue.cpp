#include "TranscriptionQueue.h"

#include <algorithm>

void TranscriptionQueue::add( const CString& inputPath )
{
	entries.push_back( { inputPath } );
}

void TranscriptionQueue::remove( const std::vector<int>& indices )
{
	std::vector<int> sorted = indices;
	std::sort( sorted.begin(), sorted.end(), std::greater<int>() );
	sorted.erase( std::unique( sorted.begin(), sorted.end() ), sorted.end() );
	for( int index : sorted )
	{
		if( index < 0 || index >= (int)entries.size() || index == running )
			continue;
		entries.erase( entries.begin() + index );
		if( index < running )
			running--;
	}
}

void TranscriptionQueue::clear()
{
	entries.clear();
	running = -1;
}

void TranscriptionQueue::reset()
{
	for( Item& item : entries )
	{
		item.state = State::Pending;
		item.result = S_OK;
	}
	running = -1;
}

int TranscriptionQueue::startNext()
{
	if( running >= 0 )
		return running;
	for( size_t i = 0; i < entries.size(); i++ )
	{
		if( entries[ i ].state != State::Pending )
			continue;
		entries[ i ].state = State::Running;
		running = (int)i;
		return running;
	}
	return -1;
}

void TranscriptionQueue::completeCurrent( HRESULT result )
{
	if( running < 0 || running >= (int)entries.size() )
		return;
	Item& item = entries[ running ];
	item.result = result;
	item.state = FAILED( result ) ? State::Failed : State::Completed;
	running = -1;
}
