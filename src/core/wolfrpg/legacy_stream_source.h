#ifndef UNPACKRAT_CORE_WOLFRPG_LEGACY_STREAM_SOURCE_H
#define UNPACKRAT_CORE_WOLFRPG_LEGACY_STREAM_SOURCE_H
#include <cstdint>
#include <streambuf>
namespace unpackrat::wolfrpg::detail
{
    // Private synchronous borrowed source. Its streambuf must outlive the operation.
    // Real I/O failures are sticky; invalid request arguments do not mark the source failed.
    struct legacy_stream_source final
    {
        std::streambuf &buffer;
        bool source_failed = false;
    };
    // destination is borrowed and has room for size bytes when size is positive.
    // Negative offset/size returns -1 before I/O; zero size retains positioning and a zero-byte read.
    // Real seek/read failures latch source_failed, preserving caller resource exceptions.
    int read_legacy_source(legacy_stream_source &source, std::int64_t offset, void *destination, int size);
} // namespace unpackrat::wolfrpg::detail
#endif
