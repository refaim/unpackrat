#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include <uberwolf/WolfRPG/FileAccess.hpp>

namespace
{
    std::vector<std::uint8_t> sample_bytes()
    {
        return {0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80};
    }

    constexpr std::uint64_t rejected_uint64_size() noexcept
    {
        if constexpr (sizeof(std::size_t) < sizeof(std::uint64_t)) {
            return static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()) + 1;
        } else {
            return static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()) + 1;
        }
    }
} // namespace

TEST_CASE("FileWriter accepts normal typed byte counts in buffer mode", "[uberwolf][fileaccess]")
{
    FileWriter writer;
    const std::array<std::uint8_t, 8> payload{1, 2, 3, 4, 5, 6, 7, 8};

    writer.WriteBytes(payload.data(), 2);
    writer.WriteBytes(payload.data(), std::uint32_t{3});
    writer.WriteBytes(payload.data(), std::size_t{3});
    writer.WriteBytesVec(std::vector<std::uint8_t>{9, 9}, 2);

    CHECK(writer.GetSize() == 10);
    CHECK(writer.GetBuffer().size() == 10);
}

TEST_CASE("FileWriter accepts boolean and character sizes that std::in_range rejects", "[uberwolf][fileaccess]")
{
    // std::in_range is only specified for the standard integer types, so the forwarding
    // overload must not pass bool or the character types to it. All of these calls must
    // compile and behave like the standard-integer overloads.
    FileWriter writer;
    const std::array<std::uint8_t, 8> payload{1, 2, 3, 4, 5, 6, 7, 8};

    writer.WriteBytes(payload.data(), true);
    writer.WriteBytes(payload.data(), static_cast<char>(2));
    writer.WriteBytes(payload.data(), static_cast<char8_t>(1));
    writer.WriteBytes(payload.data(), static_cast<wchar_t>(1));
    writer.WriteBytes(payload.data(), static_cast<char16_t>(3));
    writer.WriteBytes(payload.data(), static_cast<char32_t>(4));
    writer.WriteBytes(payload.data(), std::uint64_t{5});

    CHECK(writer.GetSize() == 17);
    CHECK(writer.GetBuffer().size() == 17);
}

TEST_CASE("FileWriter rejects a negative int size without touching its state", "[uberwolf][fileaccess]")
{
    FileWriter writer;
    const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};

    CHECK_THROWS_AS(writer.WriteBytes(payload.data(), -1), FileWriterException);
    CHECK(writer.GetSize() == 0);
    CHECK(writer.GetBuffer().empty());
}

TEST_CASE("FileWriter rejects a size_t::max size without touching its state", "[uberwolf][fileaccess]")
{
    FileWriter writer;
    const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};

    CHECK_THROWS_AS(writer.WriteBytes(payload.data(), std::numeric_limits<std::size_t>::max()), FileWriterException);
    CHECK(writer.GetSize() == 0);
    CHECK(writer.GetBuffer().empty());
}

TEST_CASE("FileWriter rejects unrepresentable uint64 sizes without touching its state", "[uberwolf][fileaccess]")
{
    const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};
    const std::uint64_t first = rejected_uint64_size();
    const std::uint64_t second = first + 1;

    for (const std::uint64_t size : {first, second}) {
        FileWriter writer;

        CHECK_THROWS_AS(writer.WriteBytes(payload.data(), size), FileWriterException);
        CHECK(writer.GetSize() == 0);
        CHECK(writer.GetBuffer().empty());
    }
}

TEST_CASE("FileWriter accepts zero-sized null writes and rejects null positive writes", "[uberwolf][fileaccess]")
{
    FileWriter writer;
    const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};

    writer.WriteBytes(nullptr, std::size_t{0});
    writer.WriteBytes(nullptr, std::uint32_t{0});
    CHECK(writer.GetSize() == 0);
    CHECK(writer.GetBuffer().empty());

    writer.WriteBytes(payload.data(), std::size_t{1});
    CHECK_THROWS_AS(writer.WriteBytes(nullptr, std::size_t{1}), FileWriterException);
    CHECK(writer.GetSize() == 1);
    CHECK(writer.GetBuffer().size() == 1);
    CHECK(writer.GetBuffer().front() == payload.front());
}

TEST_CASE("FileReader accepts an exact-end read and a zero-sized read", "[uberwolf][fileaccess]")
{
    FileReader reader(sample_bytes());
    std::array<std::uint8_t, 8> buffer{};

    REQUIRE(reader.GetSize() == 8);

    reader.ReadBytes(buffer.data(), std::size_t{0});
    REQUIRE(reader.GetOffset() == 0);

    reader.ReadBytes(buffer.data(), std::size_t{8});
    CHECK(reader.GetOffset() == 8);
    CHECK(reader.IsEoF());
    CHECK(buffer.front() == 0x10);
    CHECK(buffer.back() == 0x80);
}

TEST_CASE("FileReader rejects a read past the end of the file", "[uberwolf][fileaccess]")
{
    FileReader reader(sample_bytes());
    std::array<std::uint8_t, 16> buffer{};

    CHECK_THROWS_AS(reader.ReadBytes(buffer.data(), std::size_t{9}), FileWalkerException);
    CHECK(reader.GetOffset() == 0);
}

TEST_CASE("FileReader rejects a size_t::max read after advancing one byte", "[uberwolf][fileaccess]")
{
    FileReader reader(sample_bytes());
    std::array<std::uint8_t, 16> buffer{};

    reader.ReadBytes(buffer.data(), std::size_t{1});
    REQUIRE(reader.GetOffset() == 1);

    CHECK_THROWS_AS(reader.ReadBytes(buffer.data(), std::numeric_limits<std::size_t>::max()), FileWalkerException);
    CHECK(reader.GetOffset() == 1);
}

TEST_CASE("FileReader accepts zero-sized null reads and rejects null positive reads", "[uberwolf][fileaccess]")
{
    std::vector<std::uint8_t> empty;
    FileReader reader(empty);

    REQUIRE(reader.GetSize() == 0);
    reader.ReadBytes(empty.data(), std::size_t{0});
    reader.ReadBytes(nullptr, std::size_t{0});
    CHECK(reader.GetOffset() == 0);

    CHECK_THROWS_AS(reader.ReadBytes(nullptr, std::size_t{1}), FileWalkerException);
    CHECK(reader.GetOffset() == 0);
}

TEST_CASE("FileReader accepts a zero-sized read at the exact end", "[uberwolf][fileaccess]")
{
    FileReader reader(sample_bytes());
    std::array<std::uint8_t, 8> buffer{};

    reader.ReadBytes(buffer.data(), std::size_t{8});
    REQUIRE(reader.GetOffset() == 8);
    reader.ReadBytes(nullptr, std::size_t{0});
    CHECK(reader.GetOffset() == 8);
    CHECK(reader.IsEoF());
}
