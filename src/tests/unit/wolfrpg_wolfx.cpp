// Unit and oracle tests for the independent WolfX streaming backend. The fixtures are crafted
// from the pinned upstream generator/utils primitives and their expected plaintext, header,
// static blob and checksum are cross-checked with the unmodified upstream decryptFull oracle.

#include "../../core/wolfrpg/wolfx_archive.h"
#include "../../core/wolfrpg/wolfx_candidates.h"

#include <uberwolf/WolfX/Stream.hpp>

// CrackDetail.hpp is pinned upstream code with its own unused-variable warning; mute it only
// while that vendor header is parsed, then restore the gate for this translation unit.
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-parameter"
#pragma clang diagnostic ignored "-Wunused-variable"
#endif
#include <uberwolf/WolfX/detail/CrackDetail.hpp>
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <ios>
#include <istream>
#include <map>
#include <new>
#include <numeric>
#include <optional>
#include <ostream>
#include <sstream>
#include <stdexcept>
#include <streambuf>
#include <string>
#include <thread>
#include <type_traits>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    using unpackrat::wolfrpg::entry;
    using unpackrat::wolfrpg::key_required;
    using unpackrat::wolfrpg::read_error;
    using unpackrat::wolfrpg::wolfx_archive;
    using unpackrat::wolfrpg::wolfx_candidate_limits;
    using unpackrat::wolfrpg::wolfx_candidates;

    template <class Error> std::optional<Error> observed_exception(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const Error &error) {
            return error;
        }
        return {};
    }
    template <class Error> void require_message(const std::function<void()> &operation, const std::string_view message)
    {
        const auto error = observed_exception<Error>(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(std::string_view(error.value().what()) == message);
    }
    void require_io_failure(const std::function<void()> &operation)
    {
        const auto error = observed_exception<std::ios_base::failure>(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(error.value().code() == std::make_error_code(std::io_errc::stream));
        CHECK_FALSE(std::string_view(error.value().what()).empty());
    }
    std::uint8_t sparse_byte(const std::map<std::uint64_t, std::uint8_t> &source, std::uint64_t offset)
    {
        const auto found = source.find(offset);
        return found == source.end() ? std::uint8_t{0} : found->second;
    }
    constexpr std::size_t blob_size = 256;

    // One crafted WolfX fixture: the encrypted header, the recovered blob, the payload offset
    // and the exact checksum sample offsets for the requested physical size.
    struct crafted
    {
        std::array<std::uint8_t, 20> header{};
        std::array<std::uint8_t, blob_size> base_blob{};
        std::array<std::uint8_t, blob_size> blob{};
        std::array<std::uint8_t, 5> checksum{};
        std::array<std::uint64_t, 5> samples{};
        std::uint64_t payload_offset = 0;
    };

    std::string cycle_payload(const std::size_t length)
    {
        std::string payload(length, 'a');
        for (std::size_t i = 0; i < length; ++i)
            payload[i] = static_cast<char>('a' + (i % 26));
        return payload;
    }

    // Builds the header/blob/checksum of a crafted fixture but not its sample offsets, so a caller
    // can request a physical size whose rounded samples would not fit in a uint64.
    crafted craft_core(const std::string &key, const std::string &magic_str, const std::uint32_t magic_int,
                       const std::uint32_t string_id, const std::uint32_t int_id, const std::uint64_t physical_size)
    {
        const wolfx::WolfXKeyData key_data(key.begin(), key.end());
        const wolfx::StaticBlob static_blob = wolfx::detail::generator::generateWolfxStaticBlob(key_data);
        crafted result;
        result.payload_offset = 512U + static_blob[0] + static_blob[1];

        result.header[0] = 0x57; // 'W'
        result.header[1] = 0x4F; // 'O'
        result.header[2] = 0x4C; // 'L'
        result.header[3] = 0x46; // 'F'
        result.header[4] = 0x58; // 'X'
        result.header[5] = 0x11;
        result.header[6] = 0x22;
        result.header[7] = 0x33;
        result.header[8] = 0x44;
        result.header[9] = 0x99;

        const std::uint32_t header_int = wolfx::detail::utils::combineBytes<4u>(result.header, 5);
        const std::uint32_t seed = wolfx::detail::generator::fnv1(static_blob) ^ header_int;
        result.base_blob = wolfx::detail::generator::generateWolfxDecryptBlob(seed, static_blob,
                                                                              static_cast<std::size_t>(physical_size));

        const std::uint8_t d10 = static_cast<std::uint8_t>((string_id >> 8u) & 0xffU);
        const std::uint8_t d11 = static_cast<std::uint8_t>(string_id & 0xffU);
        result.header[10] = static_cast<std::uint8_t>(d10 ^ result.base_blob[0]);
        result.header[11] = static_cast<std::uint8_t>(d11 ^ result.base_blob[1]);
        const std::array<std::uint8_t, 2> xor_bytes = {d10, d11};

        const std::uint32_t str_hash = wolfx::detail::generator::fnv1(magic_str);
        const std::uint32_t tail_value = int_id ^ ((str_hash & 0xFFFF0000U) >> 8u) ^ (str_hash & 0xFFFFU);
        const std::array<std::uint8_t, 3> tail = wolfx::detail::utils::extractBytes<3u>(tail_value);
        for (std::size_t i = 0; i < 3u; ++i)
            result.header[12 + i] = static_cast<std::uint8_t>(tail[i] ^ result.base_blob[2 + i]);

        for (std::size_t i = 0; i < 5u; ++i)
            result.header[15 + i] = static_cast<std::uint8_t>(0xD0U + i);

        const std::array<std::uint8_t, 3> mod_val = wolfx::detail::utils::extractBytes<3u>(int_id);
        const std::uint32_t int_hash = (magic_int << 13u) ^ (73244475U * magic_int);
        const std::array<std::uint8_t, 4> int_mod = wolfx::detail::utils::extractBytes<4u>(int_hash);
        result.blob = result.base_blob;
        for (std::size_t i = 0; i < blob_size; ++i) {
            const std::uint8_t magic_char =
                magic_str.empty() ? 0 : static_cast<std::uint8_t>(magic_str[i % magic_str.size()]);
            result.blob[i] ^= static_cast<std::uint8_t>(
                static_cast<unsigned>(xor_bytes[i % 2]) ^ static_cast<unsigned>(magic_char) ^
                static_cast<unsigned>(int_mod[i & 3u]) ^ static_cast<unsigned>(mod_val[i % 3]));
        }

        for (std::size_t k = 0; k < 5u; ++k)
            result.checksum[k] = static_cast<std::uint8_t>(result.header[15 + k] ^ result.blob[5 + k]);

        return result;
    }

    // Adds the five sample offsets to a crafted fixture using the pinned upstream double rounding.
    crafted craft(const std::string &key, const std::string &magic_str, const std::uint32_t magic_int,
                  const std::uint32_t string_id, const std::uint32_t int_id, const std::uint64_t physical_size)
    {
        crafted result = craft_core(key, magic_str, magic_int, string_id, int_id, physical_size);
        const std::uint64_t data_size = physical_size - result.payload_offset;
        const std::uint64_t final_index = data_size - 1;
        for (std::size_t i = 0; i < 5u; ++i)
            result.samples[i] = result.payload_offset + static_cast<std::uint64_t>(static_cast<double>(final_index) *
                                                                                   0.25 * static_cast<double>(i));
        return result;
    }

    // The full decrypted plaintext of a crafted fixture, then its encryption.
    std::vector<std::uint8_t> craft_plaintext(const crafted &fixture, const std::string &payload)
    {
        const std::uint64_t physical_size = fixture.payload_offset + payload.size();
        std::vector<std::uint8_t> plain(static_cast<std::size_t>(physical_size));
        for (std::size_t i = 0; i < 10u; ++i)
            plain[i] = fixture.header[i];
        for (std::size_t i = 0; i < 5u; ++i)
            plain[10 + i] = static_cast<std::uint8_t>(fixture.header[10 + i] ^ fixture.base_blob[i]);
        for (std::size_t i = 0; i < 5u; ++i)
            plain[15 + i] = static_cast<std::uint8_t>(fixture.header[15 + i] ^ fixture.blob[5 + i]);
        for (std::size_t i = 0; i < payload.size(); ++i)
            plain[static_cast<std::size_t>(fixture.payload_offset) + i] = static_cast<std::uint8_t>(payload[i]);
        for (std::size_t k = 0; k < 5u; ++k)
            plain[static_cast<std::size_t>(fixture.samples[k])] = fixture.checksum[k];
        return plain;
    }

    std::vector<std::uint8_t> encrypt(const crafted &fixture, const std::vector<std::uint8_t> &plain)
    {
        std::vector<std::uint8_t> enc = plain;
        for (std::size_t i = 15; i < enc.size(); ++i)
            enc[i] = static_cast<std::uint8_t>(plain[i] ^ fixture.blob[(i - 10) % blob_size]);
        for (std::size_t i = 10; i < 15u; ++i)
            enc[i] = fixture.header[i];
        return enc;
    }

    // One crafted file, its bytes, the upstream-verified plaintext and the candidate set.
    struct file_fixture
    {
        crafted fixture;
        std::vector<std::uint8_t> encrypted;
        std::vector<std::uint8_t> plaintext;
        wolfx_candidates candidates;
    };

    file_fixture make_custom_file(const std::size_t payload_length = 200)
    {
        const std::string key = "kek";
        const std::string magic_str = "MAGICKEY";
        const std::uint32_t magic_int = 0x12345678U;
        const std::uint32_t string_id = 7;
        const std::uint32_t int_id = 4242;
        const std::string payload = cycle_payload(payload_length);

        // Choose the physical size so the payload length is exact.
        const crafted probe = craft(key, magic_str, magic_int, string_id, int_id, 4096);
        const std::uint64_t physical_size = probe.payload_offset + payload.size();
        const crafted fixture = craft(key, magic_str, magic_int, string_id, int_id, physical_size);
        const std::vector<std::uint8_t> plain = craft_plaintext(fixture, payload);

        wolfx_candidates candidates;
        candidates.add_key(key);
        candidates.add_string_value(string_id, magic_str);
        candidates.add_int_value(int_id, magic_int);
        return {fixture, encrypt(fixture, plain), plain, candidates};
    }

    file_fixture make_default_file(const std::size_t payload_length = 128)
    {
        const std::string key;
        const std::string magic_str;
        const std::uint32_t magic_int = 0;
        const std::uint32_t string_id = 20000;  // >= 10000 -> empty-string branch
        const std::uint32_t int_id = 0xFFFFF0U; // >= 1000000 -> no integer candidates
        const std::string payload = cycle_payload(payload_length);

        const crafted probe = craft(key, magic_str, magic_int, string_id, int_id, 4096);
        const std::uint64_t physical_size = probe.payload_offset + payload.size();
        const crafted fixture = craft(key, magic_str, magic_int, string_id, int_id, physical_size);
        const std::vector<std::uint8_t> plain = craft_plaintext(fixture, payload);
        return {fixture, encrypt(fixture, plain), plain, wolfx_candidates{}};
    }

    std::istringstream open_stream(const std::vector<std::uint8_t> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::in) |
                                                                       static_cast<unsigned>(std::ios::binary)));
    }

    std::istringstream open_truncated_stream(const std::vector<std::uint8_t> &bytes, const std::size_t length)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), length),
                                  static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::in) |
                                                                       static_cast<unsigned>(std::ios::binary)));
    }

    std::uint32_t upstream_data_offset = 0;

    std::pair<bool, std::vector<std::uint8_t>> upstream_decrypt(const file_fixture &file)
    {
        std::vector<std::uint8_t> dec(file.encrypted.size());
        std::copy(file.encrypted.begin(), file.encrypted.begin() + 10, dec.begin());
        std::uint32_t offset = 0;
        const bool ok = wolfx::detail::crack::decryptFull(file.encrypted, std::string("kek"), std::string("MAGICKEY"),
                                                          0x12345678U, dec, offset);
        upstream_data_offset = offset;
        return {ok, std::move(dec)};
    }

    // A random-access generated stream: serves a bounded byte source without ever materialising
    // the whole file, so a huge physical size can be modelled without allocating it.
    class generated_streambuf final : public std::streambuf
    {
      public:
        generated_streambuf(const std::uint64_t size, std::function<std::uint8_t(std::uint64_t)> source)
            : size_(size), serve_limit_(size), source_(std::move(source))
        {
        }

        void set_size(const std::uint64_t size) noexcept
        {
            size_ = size;
        }

        void set_serve_limit(const std::uint64_t limit) noexcept
        {
            serve_limit_ = limit;
        }

        void fail_end_seek(const bool enabled) noexcept
        {
            fail_end_ = enabled;
        }

      protected:
        std::streamsize xsgetn(char *out, const std::streamsize count) override
        {
            if (count <= 0)
                return 0;
            const std::uint64_t limit = std::min(size_, serve_limit_);
            const std::uint64_t available = limit > position_ ? limit - position_ : 0;
            const std::uint64_t take = std::min(available, static_cast<std::uint64_t>(count));
            for (std::uint64_t i = 0; i < take; ++i)
                out[i] = static_cast<char>(source_(position_ + i));
            position_ += take;
            return static_cast<std::streamsize>(take);
        }

        int_type underflow() override
        {
            if (position_ >= std::min(size_, serve_limit_))
                return traits_type::eof();
            current_ = static_cast<char>(source_(position_));
            return traits_type::to_int_type(current_);
        }

        int_type uflow() override
        {
            const auto value = underflow();
            if (!traits_type::eq_int_type(value, traits_type::eof()))
                ++position_;
            return value;
        }

        pos_type seekoff(const off_type off, const std::ios_base::seekdir dir, const std::ios_base::openmode) override
        {
            if (fail_end_ && dir == std::ios_base::end)
                return pos_type(off_type(-1));
            const std::int64_t base = dir == std::ios_base::beg   ? 0
                                      : dir == std::ios_base::cur ? static_cast<std::int64_t>(position_)
                                                                  : static_cast<std::int64_t>(size_);
            const std::int64_t next = base + off;
            if (next < 0u || static_cast<std::uint64_t>(next) > size_)
                return pos_type(off_type(-1));
            position_ = static_cast<std::uint64_t>(next);
            return pos_type(static_cast<off_type>(position_));
        }

        pos_type seekpos(const pos_type pos, const std::ios_base::openmode mode) override
        {
            return seekoff(static_cast<off_type>(pos), std::ios_base::beg, mode);
        }

      private:
        std::uint64_t size_;
        std::uint64_t serve_limit_ = 0;
        bool fail_end_ = false;
        std::function<std::uint8_t(std::uint64_t)> source_;
        std::uint64_t position_ = 0;
        char current_ = 0;
    };

    class failing_streambuf final : public std::streambuf
    {
      protected:
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            return 0;
        }
        int_type overflow(int_type) override
        {
            return traits_type::eof();
        }
    };

    struct cancel_signal
    {
        int code = 0;
        std::string payload;
    };

    std::string extract_to_string(const wolfx_archive &archive, std::istream &stream,
                                  const std::function<void(std::int64_t)> &progress = {})
    {
        std::ostringstream output(std::ios::binary);
        archive.extract(0, stream, output, progress);
        return output.str();
    }

    // I/O control sentinels: a source/output that throws a caller-owned exception, so the
    // local source normalisation can be shown to leave non-I/O exceptions untouched.
    struct io_sentinel
    {
        int code = 0;
        std::string payload;
    };

    class throwing_input_streambuf final : public std::streambuf
    {
      protected:
        std::streamsize xsgetn(char *, std::streamsize) override
        {
            throw io_sentinel{11, "input"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        }
        int_type underflow() override
        {
            throw io_sentinel{11, "input"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        }
        pos_type seekoff(off_type, std::ios_base::seekdir, std::ios_base::openmode) override
        {
            return pos_type(0);
        }
        pos_type seekpos(pos_type, std::ios_base::openmode) override
        {
            return pos_type(0);
        }
    };

    class throwing_output_streambuf final : public std::streambuf
    {
      protected:
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            throw io_sentinel{22, "output"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        }
        int_type overflow(int_type) override
        {
            throw io_sentinel{22, "output"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        }
    };

    // A source whose random-access reads and seeks raise a caller-supplied exception. With the
    // stream exception mask enabled the original exception travels through the standard stream
    // machinery, so it is the exact point where the local source boundary decides what to
    // normalize and what to let through.
    class exception_source_streambuf final : public std::streambuf
    {
      public:
        explicit exception_source_streambuf(std::function<void()> thrower) : thrower_(std::move(thrower))
        {
        }

      protected:
        std::streamsize xsgetn(char *, std::streamsize) override
        {
            thrower_();
            return 0;
        }
        int_type underflow() override
        {
            thrower_();
            return traits_type::eof();
        }
        pos_type seekoff(off_type, std::ios_base::seekdir, std::ios_base::openmode) override
        {
            thrower_();
            return pos_type(off_type(-1));
        }
        pos_type seekpos(pos_type, std::ios_base::openmode) override
        {
            thrower_();
            return pos_type(off_type(-1));
        }

      private:
        std::function<void()> thrower_;
    };

    // A random-access source that can fail an individual buffer operation, so the source
    // boundary's per-operation behaviour - a refused absolute seek, the seekpos fallback, the
    // end-size query and a read - can be driven directly rather than only through a formatted
    // stream. ``refuse_beg_seekoff`` models a buffer that answers seekpos but rejects plain
    // seekoff seeks; ``refuse_all_seek`` rejects both.
    class faulting_source_streambuf final : public std::streambuf
    {
      public:
        enum class fault : std::uint8_t
        {
            none,
            fail_ios,
            bad_alloc,
            length_error
        };

        explicit faulting_source_streambuf(std::vector<std::uint8_t> bytes) : bytes_(std::move(bytes))
        {
        }

        void refuse_beg_seekoff() noexcept
        {
            refuse_beg_seekoff_ = true;
        }

        void refuse_all_seek() noexcept
        {
            refuse_all_seek_ = true;
        }

        void fault_end_seek(const fault value) noexcept
        {
            end_fault_ = value;
        }

        void fault_read(const fault value) noexcept
        {
            read_fault_ = value;
        }

      protected:
        std::streamsize xsgetn(char *out, const std::streamsize count) override
        {
            fire(read_fault_);
            if (count <= 0)
                return 0;
            const std::uint64_t available = position_ < bytes_.size() ? bytes_.size() - position_ : 0;
            const std::uint64_t take = std::min(available, static_cast<std::uint64_t>(count));
            for (std::uint64_t i = 0; i < take; ++i)
                out[i] = static_cast<char>(bytes_[static_cast<std::size_t>(position_ + i)]);
            position_ += take;
            return static_cast<std::streamsize>(take);
        }

        int_type underflow() override
        {
            if (position_ >= bytes_.size())
                return traits_type::eof();
            fire(read_fault_);
            return traits_type::to_int_type(static_cast<char>(bytes_[static_cast<std::size_t>(position_)]));
        }

        int_type uflow() override
        {
            const auto value = underflow();
            if (!traits_type::eq_int_type(value, traits_type::eof()))
                ++position_;
            return value;
        }

        pos_type seekoff(const off_type off, const std::ios_base::seekdir dir, const std::ios_base::openmode) override
        {
            if (dir == std::ios_base::end)
                fire(end_fault_);
            if (refuse_all_seek_ || (refuse_beg_seekoff_ && dir == std::ios_base::beg))
                return pos_type(off_type(-1));
            return place(off, dir);
        }

        pos_type seekpos(const pos_type pos, const std::ios_base::openmode) override
        {
            if (refuse_all_seek_)
                return pos_type(off_type(-1));
            return place(static_cast<off_type>(pos), std::ios_base::beg);
        }

      private:
        pos_type place(const off_type off, const std::ios_base::seekdir dir)
        {
            const std::int64_t base = dir == std::ios_base::beg   ? 0
                                      : dir == std::ios_base::cur ? static_cast<std::int64_t>(position_)
                                                                  : static_cast<std::int64_t>(bytes_.size());
            const std::int64_t next = base + off;
            if (next < 0u || static_cast<std::uint64_t>(next) > bytes_.size())
                return pos_type(off_type(-1));
            position_ = static_cast<std::uint64_t>(next);
            return pos_type(static_cast<off_type>(position_));
        }

        void fire(const fault value)
        {
            // Every defined fault is covered. Other enum representations previously fell through too.
            if (value == fault::fail_ios)
                throw std::ios_base::failure("source");
            if (value == fault::bad_alloc)
                throw std::bad_alloc();
            if (value == fault::length_error)
                throw std::length_error("source");
        }

        std::vector<std::uint8_t> bytes_;
        std::uint64_t position_ = 0;
        bool refuse_beg_seekoff_ = false;
        bool refuse_all_seek_ = false;
        fault end_fault_ = fault::none;
        fault read_fault_ = fault::none;
    };
} // namespace

// Construction of a wolfx_archive is owned by open(). The passkey type's default constructor is
// private to wolfx_archive, so a foreign translation unit cannot fabricate an argument; every
// argument it can name (including a braced literal) is rejected at compile time. These are the
// negative compile contract, not runtime checks.
namespace
{
    template <class... Args>
    concept externally_constructible = requires(Args &&...args) { wolfx_archive{std::forward<Args>(args)...}; };

    static_assert(!externally_constructible<>);
    static_assert(!externally_constructible<int>);
    static_assert(!externally_constructible<std::string>);
    static_assert(!externally_constructible<const char *>);
    static_assert(!std::is_default_constructible_v<wolfx_archive>);
    static_assert(!std::is_copy_constructible_v<wolfx_archive>);
    static_assert(!std::is_default_constructible_v<wolfx_archive::passkey>);
    static_assert(!std::is_copy_constructible_v<wolfx_archive::passkey>);
    static_assert(!std::is_move_constructible_v<wolfx_archive::passkey>);
} // namespace

TEST_CASE("WolfX archive construction is owned by open", "[wolfrpg][wolfx]")
{
    // The static assertions above are the real check; this case keeps the contract visible to
    // the runtime suite.
    CHECK_FALSE(externally_constructible<>);
}

TEST_CASE("WolfX default empty-key archive opens and extracts", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_default_file();
    std::istringstream stream = open_stream(file.encrypted);

    const auto archive = wolfx_archive::open(stream, "movie.wolfx", file.candidates);
    REQUIRE(archive != nullptr);
    REQUIRE(archive->entries().size() == 1);
    const entry &item = archive->entries().front();
    CHECK(item.path == "movie");
    CHECK(item.stored_size == static_cast<std::int64_t>(file.plaintext.size() - file.fixture.payload_offset));
    CHECK(item.unpacked_size == item.stored_size);
    CHECK(archive->version() == 1);

    const std::string decrypted = extract_to_string(*archive, stream);
    const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                               file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
    CHECK(decrypted == expected);
}

TEST_CASE("WolfX custom key/string/int archive matches the upstream oracle", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    const auto oracle = upstream_decrypt(file);
    REQUIRE(oracle.first);
    CHECK(oracle.second == file.plaintext);
    CHECK(upstream_data_offset == file.fixture.payload_offset);
    CHECK(file.fixture.payload_offset == 577);

    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "data.wolfx", file.candidates);
    REQUIRE(archive != nullptr);
    CHECK(archive->entries().front().path == "data");

    const std::string decrypted = extract_to_string(*archive, stream);
    const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                               file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
    CHECK(decrypted == expected);
}

TEST_CASE("WolfX tries candidates in order and accepts a later key", "[wolfrpg][wolfx]")
{
    file_fixture file = make_custom_file();
    file.candidates = wolfx_candidates{};
    file.candidates.add_key("first-wrong");
    file.candidates.add_key("second-wrong");
    file.candidates.add_key("kek");
    file.candidates.add_string_value(7, "MAGICKEY");
    file.candidates.add_int_value(4242, 0x12345678U);

    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "late.wolfx", file.candidates);
    REQUIRE(archive != nullptr);
    CHECK(extract_to_string(*archive, stream).size() == file.plaintext.size() - file.fixture.payload_offset);
}

TEST_CASE("WolfX missing metadata needs a key", "[wolfrpg][wolfx]")
{
    file_fixture file = make_custom_file();
    file.candidates = wolfx_candidates{};
    file.candidates.add_key("kek"); // key present, but the string/int metadata is absent

    std::istringstream stream = open_stream(file.encrypted);
    require_message<key_required>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                  "WolfRPG archive requires a key");
}

TEST_CASE("WolfX in-range integer without a candidate is rejected through open", "[wolfrpg][wolfx]")
{
    // The key and magic string are known, so the search reaches the integer lookup; with no
    // integer candidate for the derived index the open must fail cleanly with key_required.
    file_fixture file = make_custom_file();
    file.candidates = wolfx_candidates{};
    file.candidates.add_key("kek");
    file.candidates.add_string_value(7, "MAGICKEY");

    std::istringstream stream = open_stream(file.encrypted);
    require_message<key_required>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                  "WolfRPG archive requires a key");
}

TEST_CASE("WolfX wrong checksum cannot be decrypted", "[wolfrpg][wolfx]")
{
    file_fixture file = make_custom_file();
    file.encrypted[static_cast<std::size_t>(file.fixture.samples[0])] ^= 0x01U;

    std::istringstream stream = open_stream(file.encrypted);
    require_message<key_required>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                  "WolfRPG archive requires a key");
}

TEST_CASE("WolfX structural corruption is a read error", "[wolfrpg][wolfx]")
{
    SECTION("empty stream")
    {
        std::istringstream stream;
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                    "WolfRPG archive read failed");
    }
    SECTION("truncated header")
    {
        const file_fixture file = make_custom_file();
        std::istringstream stream = open_truncated_stream(file.encrypted, 10);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }
    SECTION("bad magic")
    {
        file_fixture file = make_custom_file();
        file.encrypted[0] = 'X';
        std::istringstream stream = open_stream(file.encrypted);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }
    SECTION("payload offset past the end cannot decrypt")
    {
        const file_fixture file = make_custom_file();
        std::istringstream stream = open_truncated_stream(file.encrypted, 100);
        require_message<key_required>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                      "WolfRPG archive requires a key");
    }
}

TEST_CASE("WolfX output name is derived from the path", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_default_file();
    const auto open_with = [&](const std::string &path) {
        std::istringstream stream = open_stream(file.encrypted);
        return wolfx_archive::open(stream, path, file.candidates);
    };

    CHECK(open_with("folder/sub/name.wolfx")->entries().front().path == "name");
    CHECK(open_with("UPPER.WOLFX")->entries().front().path == "UPPER");
    CHECK(open_with("plain")->entries().front().path == "plain");
    CHECK(open_with("odd.wolfy")->entries().front().path == "odd.wolfy");

    require_message<read_error>([&] { (void)(open_with(".wolfx")); }, "WolfRPG archive read failed");
    require_message<read_error>([&] { (void)(open_with("..wolfx")); }, "WolfRPG archive read failed");
    require_message<read_error>([&] { (void)(open_with("...wolfx")); }, "WolfRPG archive read failed");
    require_message<read_error>([&] { (void)(open_with("")); }, "WolfRPG archive read failed");
    require_message<read_error>([&] { (void)(open_with("a:b.wolfx")); }, "WolfRPG archive read failed");
    require_message<read_error>([&] { (void)(open_with("a\tb.wolfx")); }, "WolfRPG archive read failed");
}

TEST_CASE("WolfX huge physical size is handled without allocating it", "[wolfrpg][wolfx]")
{
    const std::string key = "kek";
    const std::uint64_t physical_size = 1ULL << 40u;
    const crafted fixture = craft(key, "MAGICKEY", 0x12345678U, 7, 4242, physical_size);

    std::map<std::uint64_t, std::uint8_t> known;
    for (std::size_t i = 0; i < 20u; ++i)
        known[i] = fixture.header[i];
    for (std::size_t k = 0; k < 5u; ++k)
        known[fixture.samples[k]] =
            static_cast<std::uint8_t>(fixture.checksum[k] ^ fixture.blob[(fixture.samples[k] - 10) % blob_size]);

    generated_streambuf buffer(physical_size,
                               [&known](const std::uint64_t offset) { return sparse_byte(known, offset); });
    std::istream stream(&buffer);

    wolfx_candidates candidates;
    candidates.add_key(key);
    candidates.add_string_value(7, "MAGICKEY");
    candidates.add_int_value(4242, 0x12345678U);

    const auto archive = wolfx_archive::open(stream, "huge.wolfx", candidates);
    REQUIRE(archive != nullptr);
    CHECK(archive->entries().front().stored_size == static_cast<std::int64_t>(physical_size - fixture.payload_offset));
    CHECK(archive->version() == 1);
}

TEST_CASE("WolfX candidate preparation samples only bounded offsets", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    std::vector<std::uint64_t> reads;
    const auto plan = wolfx::stream::prepare(
        file.fixture.header, file.encrypted.size(),
        [&] {
            wolfx::WolfXDecryptCollection collection;
            collection.decryptKeys.push_back({"/", ""});
            collection.decryptKeys.push_back({"/", std::string("kek")});
            collection.stringValues[7].insert("MAGICKEY");
            collection.intValues[4242].insert(0x12345678U);
            return collection;
        }(),
        [&](const std::uint64_t offset) {
            reads.push_back(offset);
            return file.encrypted[static_cast<std::size_t>(offset)];
        },
        1000000U);

    REQUIRE(plan.found);
    CHECK(plan.payloadOffset == file.fixture.payload_offset);
    for (const std::uint64_t offset : reads) {
        CHECK(offset >= 512U);
        CHECK(offset < file.encrypted.size());
    }
    REQUIRE(reads.size() >= 5);
    CHECK(std::equal(file.fixture.samples.begin(), file.fixture.samples.end(), reads.end() - 5));
}

TEST_CASE("WolfX extraction streams a multi-chunk payload", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file(300000);
    REQUIRE(file.plaintext.size() - file.fixture.payload_offset > std::uint64_t{64} * 1024U);

    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "big.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    std::vector<std::int64_t> deltas;
    const std::string decrypted =
        extract_to_string(*archive, stream, [&deltas](const std::int64_t delta) { deltas.push_back(delta); });
    const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                               file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
    CHECK(decrypted == expected);
    CHECK(deltas.size() > 1u);
    CHECK(std::accumulate(deltas.begin(), deltas.end(), std::int64_t{0}) == static_cast<std::int64_t>(expected.size()));
}

TEST_CASE("WolfX truncated source stream raises a read error", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    generated_streambuf buffer(file.encrypted.size(), [&file](const std::uint64_t offset) {
        return file.encrypted[static_cast<std::size_t>(offset)];
    });
    std::istream stream(&buffer);

    const auto archive = wolfx_archive::open(stream, "cut.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    buffer.set_size(file.fixture.payload_offset + 4);
    std::ostringstream output(std::ios::binary);
    require_message<read_error>([&] { (void)(archive->extract(0, stream, output, {})); },
                                "WolfRPG archive read failed");
}

TEST_CASE("WolfX stream without a valid end position is rejected", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_default_file();
    generated_streambuf buffer(file.encrypted.size(), [&file](const std::uint64_t offset) {
        return file.encrypted[static_cast<std::size_t>(offset)];
    });
    buffer.fail_end_seek(true);
    std::istream stream(&buffer);

    require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "noend.wolfx", file.candidates)); },
                                "WolfRPG archive read failed");
}

TEST_CASE("WolfX exception-enabled end seek failure is normalized", "[wolfrpg][wolfx]")
{
    // With the state mask armed, a failed seek to the end throws std::ios_base::failure from inside
    // the reader; the narrow source boundary must collapse that to read_error just like the
    // state-only case above, never letting the ios failure escape.
    const file_fixture file = make_default_file();
    generated_streambuf buffer(file.encrypted.size(), [&file](const std::uint64_t offset) {
        return file.encrypted[static_cast<std::size_t>(offset)];
    });
    buffer.fail_end_seek(true);
    std::istream stream(&buffer);
    stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                          static_cast<unsigned>(std::ios::badbit)));

    require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "noend.wolfx", file.candidates)); },
                                "WolfRPG archive read failed");
}

TEST_CASE("WolfX sample read past the available data is a read error", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_default_file();
    std::map<std::uint64_t, std::uint8_t> readable;
    for (std::size_t at = 0; at < file.encrypted.size(); ++at)
        readable.emplace(at, file.encrypted[at]);
    generated_streambuf buffer(4096, [&readable](const std::uint64_t offset) { return sparse_byte(readable, offset); });
    buffer.set_serve_limit(20); // the header is readable, the checksum samples are not
    std::istream stream(&buffer);

    require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "short.wolfx", file.candidates)); },
                                "WolfRPG archive read failed");
}

TEST_CASE("WolfX failing output stream raises a read error", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "out.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    failing_streambuf buffer;
    std::ostream output(&buffer);
    require_message<read_error>([&] { (void)(archive->extract(0, stream, output, {})); },
                                "WolfRPG archive read failed");
}

TEST_CASE("WolfX progress cancellation propagates and preserves output", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file(300000);
    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "cancel.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    std::ostringstream output(std::ios::binary);
    int calls = 0;
    const auto observed = observed_exception<cancel_signal>([&] {
        archive->extract(0, stream, output, [&calls](const std::int64_t) {
            if (++calls == 2)
                throw cancel_signal{7, "stop"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        });
    });
    REQUIRE(observed.has_value());
    if (!observed.has_value()) {
        return;
    }
    const auto &signal = observed.value();
    CHECK(signal.code == 7);
    CHECK(signal.payload == "stop");

    const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                               file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
    const std::string got = output.str();
    CHECK(got.size() == static_cast<std::size_t>(2U * 32U * 1024U));
    CHECK(got == expected.substr(0, got.size()));
}

TEST_CASE("WolfX entry index bounds and moves are safe", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    std::istringstream stream = open_stream(file.encrypted);
    auto archive = wolfx_archive::open(stream, "move.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    std::ostringstream output(std::ios::binary);
    require_message<std::out_of_range>([&] { (void)(archive->extract(1, stream, output, {})); },
                                       "WolfX contains a single entry");

    auto moved = std::move(*archive);
    CHECK(archive->entries().empty());
    require_message<std::out_of_range>([&] { (void)(archive->extract(0, stream, output, {})); },
                                       "WolfX contains a single entry");
    CHECK(moved.entries().size() == 1);

    std::ostringstream second;
    moved.extract(0, stream, second, {});
    CHECK(second.str().size() == file.plaintext.size() - file.fixture.payload_offset);

    std::istringstream other_stream = open_stream(file.encrypted);
    auto other = wolfx_archive::open(other_stream, "other.wolfx", file.candidates);
    REQUIRE(other != nullptr);
    *other = std::move(moved);
    CHECK(moved.entries().empty()); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
    CHECK(other->entries().size() == 1);
}

TEST_CASE("WolfX candidate budgets reject oversized input", "[wolfrpg][wolfx]")
{
    wolfx_candidate_limits tight;
    tight.max_key_length = 4;
    tight.max_total_key_bytes = 6;
    tight.max_string_values = 1;
    tight.max_total_string_bytes = 4;
    tight.max_int_values = 1;
    wolfx_candidates candidates(tight);

    require_message<std::invalid_argument>([&] { (void)(candidates.add_key("12345")); },
                                           "WolfX candidate key exceeds max_key_length");
    candidates.add_key("abcd");
    candidates.add_key("ef");
    require_message<std::invalid_argument>([&] { (void)(candidates.add_key("gh")); },
                                           "WolfX candidate keys exceed max_total_key_bytes");

    candidates.add_string_value(1, "abcd");
    require_message<std::invalid_argument>([&] { (void)(candidates.add_string_value(2, "xy")); },
                                           "WolfX candidate strings exceed max_string_values"); // count limit

    candidates.add_int_value(1, 2);
    require_message<std::invalid_argument>([&] { (void)(candidates.add_int_value(3, 4)); },
                                           "WolfX candidate integers exceed max_int_values");

    CHECK(candidates.keys().size() == 2);
    CHECK(candidates.string_values().size() == 1);
    CHECK(candidates.int_values().size() == 1);
    CHECK(candidates.limits().max_key_length == 4);

    // A separate limit isolates the string-byte budget from the string-count budget.
    wolfx_candidate_limits byte_limited;
    byte_limited.max_string_values = 8;
    byte_limited.max_total_string_bytes = 4;
    wolfx_candidates strings(byte_limited);
    strings.add_string_value(1, "abcd");
    require_message<std::invalid_argument>([&] { (void)(strings.add_string_value(2, "xy")); },
                                           "WolfX candidate strings exceed max_total_string_bytes");
    CHECK(strings.string_values().size() == 1);
}

TEST_CASE("WolfX attempt budget exhaustion cannot decrypt", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();

    wolfx_candidate_limits empty_budget;
    empty_budget.max_attempts = 0;
    wolfx_candidates candidates(empty_budget);
    candidates.add_key("kek");
    candidates.add_string_value(7, "MAGICKEY");
    candidates.add_int_value(4242, 0x12345678U);

    std::istringstream stream = open_stream(file.encrypted);
    require_message<key_required>([&] { (void)(wolfx_archive::open(stream, "budget.wolfx", candidates)); },
                                  "WolfRPG archive requires a key");
}

TEST_CASE("WolfX contexts are independent and concurrent", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();

    SECTION("a successful open never caches across archives")
    {
        std::istringstream first = open_stream(file.encrypted);
        REQUIRE(wolfx_archive::open(first, "one.wolfx", file.candidates) != nullptr);

        std::istringstream second = open_stream(file.encrypted);
        require_message<key_required>([&] { (void)(wolfx_archive::open(second, "two.wolfx", wolfx_candidates{})); },
                                      "WolfRPG archive requires a key");

        std::istringstream third = open_stream(file.encrypted);
        wolfx_candidates wrong;
        wrong.add_key("other");
        require_message<key_required>([&] { (void)(wolfx_archive::open(third, "three.wolfx", wrong)); },
                                      "WolfRPG archive requires a key");
    }

    SECTION("two archives decrypt concurrently")
    {
        const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                                   file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
        std::string results[2];
        std::thread threads[2];
        for (int i = 0; i < 2u; ++i) {
            threads[i] = std::thread([&, i] {
                std::istringstream stream = open_stream(file.encrypted);
                const auto archive = wolfx_archive::open(stream, "thread.wolfx", file.candidates);
                results[i] = extract_to_string(*archive, stream);
            });
        }
        for (auto &thread : threads)
            thread.join();
        CHECK(results[0] == expected);
        CHECK(results[1] == expected);
    }
}

TEST_CASE("WolfX implicit empty candidate key is rejected", "[wolfrpg][wolfx]")
{
    // The empty key is implicit (tried first by the backend), so storing it would only add an
    // unbounded, zero-cost key. Rejecting it keeps the byte budget a true bound.
    wolfx_candidates candidates;
    for (int i = 0; i < 64u; ++i)
        require_message<std::invalid_argument>([&] { (void)(candidates.add_key("")); },
                                               "WolfX candidate key must not be empty");
    CHECK(candidates.keys().empty());

    // Non-empty keys are charged to the byte budget, duplicates included, so a run of repeats
    // cannot grow the candidate set for free.
    wolfx_candidate_limits limits;
    limits.max_key_length = 5;
    limits.max_total_key_bytes = 5;
    wolfx_candidates bounded(limits);
    bounded.add_key("abcd");
    require_message<std::invalid_argument>([&] { (void)(bounded.add_key("abcd")); },
                                           "WolfX candidate keys exceed max_total_key_bytes");
    require_message<std::invalid_argument>([&] { (void)(bounded.add_key("")); },
                                           "WolfX candidate key must not be empty");
    bounded.add_key("e");
    CHECK(bounded.keys().size() == 2);
    require_message<std::invalid_argument>([&] { (void)(bounded.add_key("f")); },
                                           "WolfX candidate keys exceed max_total_key_bytes");
}

TEST_CASE("WolfX source stream failures are normalized to read_error", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();

    SECTION("exception-enabled short source during open")
    {
        std::istringstream stream = open_truncated_stream(file.encrypted, 10);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }

    SECTION("exception-enabled short source during extract")
    {
        std::istringstream stream = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(stream, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        REQUIRE(file.encrypted.size() >= 4);
        REQUIRE(file.fixture.payload_offset <= file.encrypted.size() - 4);
        const auto truncated_size = static_cast<std::size_t>(file.fixture.payload_offset) + 4;
        std::istringstream truncated = open_truncated_stream(file.encrypted, truncated_size);
        truncated.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                                 static_cast<unsigned>(std::ios::badbit)));
        std::ostringstream output(std::ios::binary);
        require_message<read_error>([&] { (void)(archive->extract(0, truncated, output, {})); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a caller-owned source exception is not rewritten")
    {
        // With the state mask enabled the source's own exception survives the stream, so a
        // narrow ios_base::failure catch keeps it intact instead of collapsing it to read_error.
        throwing_input_streambuf buffer;
        std::istream stream(&buffer);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        const auto observed =
            observed_exception<io_sentinel>([&] { wolfx_archive::open(stream, "x.wolfx", file.candidates); });
        REQUIRE(observed.has_value());
        if (!observed.has_value()) {
            return;
        }
        const auto &signal = observed.value();
        CHECK(signal.code == 11);
        CHECK(signal.payload == "input");
    }
}

TEST_CASE("WolfX masked source I/O failures normalize inside the source boundary", "[wolfrpg][wolfx]")
{
    const auto as_ios_failure = [] { throw std::ios_base::failure("source"); };
    const auto as_bad_alloc = [] { throw std::bad_alloc(); };
    const auto as_length_error = [] { throw std::length_error("source"); };

    SECTION("a source throwing ios_base::failure with the mask on is normalized on open")
    {
        exception_source_streambuf buffer(as_ios_failure);
        std::istream stream(&buffer);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source throwing ios_base::failure with the mask on is normalized on extract")
    {
        const file_fixture file = make_default_file();
        std::istringstream good = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(good, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        exception_source_streambuf buffer(as_ios_failure);
        std::istream stream(&buffer);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        std::ostringstream output(std::ios::binary);
        require_message<read_error>([&] { (void)(archive->extract(0, stream, output, {})); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source throwing bad_alloc with the mask on propagates unchanged")
    {
        exception_source_streambuf buffer(as_bad_alloc);
        std::istream stream(&buffer);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        require_message<std::bad_alloc>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                        std::bad_alloc{}.what());
    }

    SECTION("a source throwing length_error with the mask on propagates unchanged")
    {
        exception_source_streambuf buffer(as_length_error);
        std::istream stream(&buffer);
        stream.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        require_message<std::length_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); }, "source");
    }

    SECTION("a source throwing ios_base::failure with the mask off is still normalized on open")
    {
        // With the mask off the formatted istream layer would swallow the failure into the state
        // bits; driving the buffer directly keeps the normalization and never leaks ios failure.
        exception_source_streambuf buffer(as_ios_failure);
        std::istream stream(&buffer);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source throwing ios_base::failure with the mask off is still normalized on extract")
    {
        const file_fixture file = make_default_file();
        std::istringstream good = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(good, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        exception_source_streambuf buffer(as_ios_failure);
        std::istream stream(&buffer);
        std::ostringstream output(std::ios::binary);
        require_message<read_error>([&] { (void)(archive->extract(0, stream, output, {})); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source throwing bad_alloc with the mask off propagates unchanged on open")
    {
        exception_source_streambuf buffer(as_bad_alloc);
        std::istream stream(&buffer);
        require_message<std::bad_alloc>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                        std::bad_alloc{}.what());
    }

    SECTION("a source throwing length_error with the mask off propagates type and message on open")
    {
        exception_source_streambuf buffer(as_length_error);
        std::istream stream(&buffer);
        const auto observed = observed_exception<std::length_error>([&] { wolfx_archive::open(stream, "x.wolfx"); });
        REQUIRE(observed.has_value());
        if (!observed.has_value()) {
            return;
        }
        const auto &error = observed.value();
        CHECK(std::string(error.what()) == "source");
    }

    SECTION("a source throwing bad_alloc with the mask off propagates unchanged on extract")
    {
        const file_fixture file = make_default_file();
        std::istringstream good = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(good, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        exception_source_streambuf buffer(as_bad_alloc);
        std::istream stream(&buffer);
        std::ostringstream output(std::ios::binary);
        require_message<std::bad_alloc>([&] { (void)(archive->extract(0, stream, output, {})); },
                                        std::bad_alloc{}.what());
    }

    SECTION("a source throwing length_error with the mask off propagates type and message on extract")
    {
        const file_fixture file = make_default_file();
        std::istringstream good = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(good, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        exception_source_streambuf buffer(as_length_error);
        std::istream stream(&buffer);
        std::ostringstream output(std::ios::binary);
        const auto observed = observed_exception<std::length_error>([&] { archive->extract(0, stream, output, {}); });
        REQUIRE(observed.has_value());
        if (!observed.has_value()) {
            return;
        }
        const auto &error = observed.value();
        CHECK(std::string(error.what()) == "source");
    }

    SECTION("a masked null source buffer is normalized on open")
    {
        // A null source buffer whose exception mask already carries badbit: the stream's own
        // clear() re-arms the failure, so clearing must sit inside the narrow source boundary.
        std::istream stream(nullptr);
        try {
            stream.exceptions(std::ios::badbit);
        } catch (const std::ios_base::failure &error) {
            // The mask sticks with the null buffer; this is the state under test.
            static_cast<void>(error);
        }
        REQUIRE(stream.exceptions() == std::ios::badbit);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a quiet null source buffer is normalized on open")
    {
        std::istream stream(nullptr);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx")); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a masked null source buffer is normalized on extract")
    {
        const file_fixture file = make_default_file();
        std::istringstream good = open_stream(file.encrypted);
        const auto archive = wolfx_archive::open(good, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        std::istream stream(nullptr);
        try {
            stream.exceptions(std::ios::badbit);
        } catch (const std::ios_base::failure &error) {
            static_cast<void>(error); // The mask sticks with the null buffer; this is the state under test.
        }
        REQUIRE(stream.exceptions() == std::ios::badbit);
        std::ostringstream output(std::ios::binary);
        require_message<read_error>([&] { (void)(archive->extract(0, stream, output, {})); },
                                    "WolfRPG archive read failed");
    }
}

TEST_CASE("WolfX source boundary normalizes each buffer operation directly", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_default_file();

    SECTION("a seekpos-only source is served through the seekoff fallback")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.refuse_beg_seekoff();
        std::istream stream(&buffer);

        const auto archive = wolfx_archive::open(stream, "fallback.wolfx", file.candidates);
        REQUIRE(archive != nullptr);
        const std::string decrypted = extract_to_string(*archive, stream);
        const std::string expected(reinterpret_cast<const char *>(file.plaintext.data() + file.fixture.payload_offset),
                                   file.plaintext.size() - static_cast<std::size_t>(file.fixture.payload_offset));
        CHECK(decrypted == expected);
    }

    SECTION("a source rejecting every seek is a read error")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.refuse_all_seek();
        std::istream stream(&buffer);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source whose read throws ios_base::failure is a read error")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.fault_read(faulting_source_streambuf::fault::fail_ios);
        std::istream stream(&buffer);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source whose end-size seek throws ios_base::failure is a read error")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.fault_end_seek(faulting_source_streambuf::fault::fail_ios);
        std::istream stream(&buffer);
        require_message<read_error>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                    "WolfRPG archive read failed");
    }

    SECTION("a source whose header read throws bad_alloc propagates the resource error")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.fault_read(faulting_source_streambuf::fault::bad_alloc);
        std::istream stream(&buffer);
        require_message<std::bad_alloc>([&] { (void)(wolfx_archive::open(stream, "x.wolfx", file.candidates)); },
                                        std::bad_alloc{}.what());
    }

    SECTION("a source whose end-size seek throws length_error propagates type and message")
    {
        faulting_source_streambuf buffer(file.encrypted);
        buffer.fault_end_seek(faulting_source_streambuf::fault::length_error);
        std::istream stream(&buffer);
        const auto observed =
            observed_exception<std::length_error>([&] { wolfx_archive::open(stream, "x.wolfx", file.candidates); });
        REQUIRE(observed.has_value());
        if (!observed.has_value()) {
            return;
        }
        const auto &error = observed.value();
        CHECK(std::string(error.what()) == "source");
    }

    SECTION("a mid-extract read that throws bad_alloc propagates the resource error")
    {
        faulting_source_streambuf buffer(file.encrypted);
        std::istream stream(&buffer);
        const auto archive = wolfx_archive::open(stream, "x.wolfx", file.candidates);
        REQUIRE(archive != nullptr);

        buffer.fault_read(faulting_source_streambuf::fault::bad_alloc);
        std::ostringstream output(std::ios::binary);
        require_message<std::bad_alloc>([&] { (void)(archive->extract(0, stream, output, {})); },
                                        std::bad_alloc{}.what());
    }
}

TEST_CASE("WolfX output stream exceptions are preserved", "[wolfrpg][wolfx]")
{
    const file_fixture file = make_custom_file();
    std::istringstream stream = open_stream(file.encrypted);
    const auto archive = wolfx_archive::open(stream, "out.wolfx", file.candidates);
    REQUIRE(archive != nullptr);

    SECTION("a caller-owned output exception is not rewritten")
    {
        throwing_output_streambuf buffer;
        std::ostream output(&buffer);
        output.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        const auto observed = observed_exception<io_sentinel>([&] { archive->extract(0, stream, output, {}); });
        REQUIRE(observed.has_value());
        if (!observed.has_value()) {
            return;
        }
        const auto &signal = observed.value();
        CHECK(signal.code == 22);
        CHECK(signal.payload == "output");
    }

    SECTION("exception-enabled output failure is not rewritten to read_error")
    {
        failing_streambuf buffer;
        std::ostream output(&buffer);
        output.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                              static_cast<unsigned>(std::ios::badbit)));
        require_io_failure([&] { (void)(archive->extract(0, stream, output, {})); });
    }
}

TEST_CASE("WolfX stream search handles bounded structural branches", "[wolfrpg][wolfx]")
{
    const std::uint64_t physical = 4096;
    const crafted custom = craft("kek", "MAGICKEY", 0x12345678U, 7, 4242, physical);
    const crafted plain = craft("", "", 0U, 20000, 0xFFFFF0U, physical);

    std::map<std::uint64_t, std::uint8_t> bytes;
    std::vector<std::uint64_t> reads;
    // One reader shared by every section, so the early-out branches and a full successful search all
    // run through a single prepare instantiation.
    const auto read = [&bytes, &reads](const std::uint64_t pos) {
        reads.push_back(pos);
        return sparse_byte(bytes, pos);
    };

    SECTION("a physical size below the header is rejected without sampling")
    {
        reads.clear();
        bytes.clear();
        const wolfx::WolfXDecryptCollection collection;
        const auto plan = wolfx::stream::prepare(plain.header, 19, collection, read, 1000000U);
        CHECK_FALSE(plan.found);
        CHECK(reads.empty());
    }

    SECTION("attempt budget exhaustion stops before the next key")
    {
        reads.clear();
        bytes.clear();
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = plain.header[i];
        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", ""});
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        const auto plan = wolfx::stream::prepare(plain.header, physical, collection, read, 0U);
        CHECK_FALSE(plan.found);
        CHECK(reads.empty());
    }

    SECTION("an in-range integer index without a candidate is skipped")
    {
        reads.clear();
        bytes.clear();
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = custom.header[i];
        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        collection.stringValues[7].insert("MAGICKEY");
        const auto plan = wolfx::stream::prepare(custom.header, physical, collection, read, 1000000U);
        CHECK_FALSE(plan.found);
        CHECK(reads.empty());
    }

    SECTION("the shared reader still decrypts a full candidate set")
    {
        reads.clear();
        bytes.clear();
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = custom.header[i];
        for (std::size_t k = 0; k < 5u; ++k)
            bytes[custom.samples[k]] =
                static_cast<std::uint8_t>(custom.checksum[k] ^ custom.blob[(custom.samples[k] - 10) % blob_size]);
        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        collection.stringValues[7].insert("MAGICKEY");
        collection.intValues[4242].insert(0x12345678U);
        const auto plan = wolfx::stream::prepare(custom.header, physical, collection, read, 1000000U);
        REQUIRE(plan.found);
        CHECK(plan.payloadOffset == custom.payload_offset);
        CHECK(reads.size() == 5);
    }
}

TEST_CASE("WolfX sparse sample positions are bounded before reading", "[wolfrpg][wolfx]")
{
    SECTION("a >2u^53u file whose rounded last sample lands past the end is rejected")
    {
        const std::uint64_t physical = 45035996273706160ULL;
        const crafted fixture = craft("kek", "MAGICKEY", 0x12345678U, 7, 4242, physical);
        REQUIRE(fixture.samples[4] == physical + 1); // documents the pinned double-rounding hazard

        std::map<std::uint64_t, std::uint8_t> bytes;
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = fixture.header[i];
        for (std::size_t k = 0; k < 4u; ++k) // the first four samples are still in range
            bytes[fixture.samples[k]] =
                static_cast<std::uint8_t>(fixture.checksum[k] ^ fixture.blob[(fixture.samples[k] - 10) % blob_size]);

        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        collection.stringValues[7].insert("MAGICKEY");
        collection.intValues[4242].insert(0x12345678U);

        std::vector<std::uint64_t> reads;
        const auto plan = wolfx::stream::prepare(
            fixture.header, physical, collection,
            [&bytes, &reads](const std::uint64_t pos) {
                reads.push_back(pos);
                return sparse_byte(bytes, pos);
            },
            1000000U);
        CHECK_FALSE(plan.found);
        for (const std::uint64_t pos : reads)
            CHECK(pos < physical);
    }

    SECTION("a >2u^53u file with in-range samples still decrypts")
    {
        const crafted probe = craft("kek", "MAGICKEY", 0x12345678U, 7, 4242, 4096);
        const std::uint64_t physical = probe.payload_offset + (1ULL << 53u) + 1;
        const crafted fixture = craft("kek", "MAGICKEY", 0x12345678U, 7, 4242, physical);

        // The payload is 2^53u + 1 bytes, so the exact final index is 2^53u and the rounded last
        // sample (577 + 2^53u) is still the last byte inside the file. A double comparison of the
        // rounded offset against the data size cannot represent the +1 and must not reject it.
        REQUIRE(fixture.payload_offset == 577);
        REQUIRE(physical == 9007199254741570ULL);
        REQUIRE(fixture.samples[4] == 9007199254741569ULL);
        REQUIRE(fixture.samples[4] < physical);

        std::map<std::uint64_t, std::uint8_t> bytes;
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = fixture.header[i];
        for (std::size_t k = 0; k < 5u; ++k)
            bytes[fixture.samples[k]] =
                static_cast<std::uint8_t>(fixture.checksum[k] ^ fixture.blob[(fixture.samples[k] - 10) % blob_size]);

        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        collection.stringValues[7].insert("MAGICKEY");
        collection.intValues[4242].insert(0x12345678U);

        std::vector<std::uint64_t> reads;
        const auto plan = wolfx::stream::prepare(
            fixture.header, physical, collection,
            [&bytes, &reads](const std::uint64_t pos) {
                reads.push_back(pos);
                return sparse_byte(bytes, pos);
            },
            1000000U);
        REQUIRE(plan.found);
        CHECK(plan.payloadOffset == fixture.payload_offset);
        for (const std::uint64_t pos : reads)
            CHECK(pos < physical);
    }

    SECTION("a near-2^64u file whose rounded last sample overflows the offset is rejected")
    {
        const std::uint64_t physical = 0xFFFFFFFFFFFFFFFFULL; // 2^64u - 1
        const crafted fixture = craft_core("kek", "MAGICKEY", 0x12345678U, 7, 4242, physical);
        REQUIRE(fixture.payload_offset == 577);

        // dataSize is 2^64u - 578, so the final index rounds up to 2^64u; converting the fifth sample
        // offset into a 64-bit integer would overflow, so the search must reject the candidate
        // before it reads that sample instead of wrapping the position.
        const std::uint64_t data_size = physical - fixture.payload_offset;
        const std::uint64_t final_index = data_size - 1;

        std::map<std::uint64_t, std::uint8_t> bytes;
        for (std::size_t i = 0; i < 20u; ++i)
            bytes[i] = fixture.header[i];
        std::array<std::uint64_t, 4> in_range{};
        for (std::size_t k = 0; k < 4u; ++k) { // only the first four samples stay inside a uint64
            in_range[k] = fixture.payload_offset +
                          static_cast<std::uint64_t>(static_cast<double>(final_index) * 0.25 * static_cast<double>(k));
            bytes[in_range[k]] =
                static_cast<std::uint8_t>(fixture.checksum[k] ^ fixture.blob[(in_range[k] - 10) % blob_size]);
        }

        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", "kek"});
        collection.stringValues[7].insert("MAGICKEY");
        collection.intValues[4242].insert(0x12345678U);

        std::vector<std::uint64_t> reads;
        const auto plan = wolfx::stream::prepare(
            fixture.header, physical, collection,
            [&bytes, &reads](const std::uint64_t pos) {
                reads.push_back(pos);
                return sparse_byte(bytes, pos);
            },
            1000000U);
        CHECK_FALSE(plan.found);
        CHECK(reads == std::vector<std::uint64_t>(in_range.begin(), in_range.end()));
    }
}

// The whole-buffer crackers are now a thin delegation over the same shared search the streaming
// backend uses. These cases fail to compile against the pre-refactor vendor headers (there was no
// shared helper API), so they pin the delegation and cross-check its output against the frozen
// whole-buffer oracle and the streaming path.
TEST_CASE("WolfX whole-buffer cracker delegates to the shared search engine", "[wolfrpg][wolfx]")
{
    const file_fixture custom = make_custom_file();
    const std::string key_text = "kek";
    const wolfx::WolfXKeyData key(key_text.begin(), key_text.end());

    wolfx::WolfXDecryptCollection full;
    full.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", key_text});
    full.stringValues[7].insert("MAGICKEY");
    full.intValues[4242].insert(0x12345678U);

    const auto make_result = [&custom] {
        wolfx::DecryptResult result;
        result.decData.assign(custom.encrypted.size(), 0);
        std::copy(custom.encrypted.begin(), custom.encrypted.begin() + 10, result.decData.begin());
        return result;
    };

    SECTION("the candidate search decrypts exactly like the frozen oracle")
    {
        wolfx::DecryptResult result = make_result();
        REQUIRE(wolfx::detail::crack::tryDecryptP1(custom.encrypted, key, full, result));
        CHECK(result.success);
        CHECK(result.dataOffset == custom.fixture.payload_offset);
        CHECK(result.magicStr == "MAGICKEY");
        CHECK(result.magicInt == 0x12345678U);
        CHECK(result.decData == custom.plaintext);

        // The streaming reader drives the same engine and lands on the same plan, so the shared
        // algorithm is genuinely one implementation rather than two.
        const auto plan = wolfx::stream::prepare(
            custom.fixture.header, custom.encrypted.size(), full,
            [&custom](const std::uint64_t pos) { return custom.encrypted[static_cast<std::size_t>(pos)]; }, 1000000U);
        REQUIRE(plan.found);
        CHECK(plan.payloadOffset == custom.fixture.payload_offset);
        for (std::size_t i = 0; i < 64u; ++i) {
            const std::size_t pos = static_cast<std::size_t>(custom.fixture.payload_offset) + i;
            CHECK(static_cast<std::uint8_t>(custom.encrypted[pos] ^ plan.blob[(pos - 10) % blob_size]) ==
                  custom.plaintext[pos]);
        }
    }

    SECTION("the remembered seed replays the string and integer directly")
    {
        wolfx::DecryptResult result = make_result();
        result.success = true;
        result.magicStr = "MAGICKEY";
        result.magicInt = 0x12345678U;
        REQUIRE(wolfx::detail::crack::tryDecryptP1(custom.encrypted, key, full, result));
        CHECK(result.decData == custom.plaintext);

        // A wrong remembered integer must not be reported as a success.
        wolfx::DecryptResult wrong = make_result();
        wrong.success = true;
        wrong.magicStr = "MAGICKEY";
        wrong.magicInt = 0xDEADBEEFU;
        CHECK_FALSE(wolfx::detail::crack::tryDecryptP1(custom.encrypted, key, full, wrong));
    }

    SECTION("an absent string set ends the key without an attempt")
    {
        wolfx::WolfXDecryptCollection only_key;
        only_key.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", key_text});
        wolfx::DecryptResult result = make_result();
        CHECK_FALSE(wolfx::detail::crack::tryDecryptP1(custom.encrypted, key, only_key, result));
    }

    SECTION("a derived integer index without a candidate is skipped")
    {
        wolfx::WolfXDecryptCollection no_int;
        no_int.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", key_text});
        no_int.stringValues[7].insert("MAGICKEY");
        wolfx::DecryptResult result = make_result();
        CHECK_FALSE(wolfx::detail::crack::tryDecryptP1(custom.encrypted, key, no_int, result));
    }

    SECTION("the empty-string branch decrypts the default file")
    {
        const file_fixture plain = make_default_file();
        wolfx::WolfXDecryptCollection none;
        none.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", ""});
        wolfx::DecryptResult result;
        result.decData.assign(plain.encrypted.size(), 0);
        std::copy(plain.encrypted.begin(), plain.encrypted.begin() + 10, result.decData.begin());
        REQUIRE(wolfx::detail::crack::tryDecryptP1(plain.encrypted, wolfx::WolfXKeyData{}, none, result));
        CHECK(result.decData == plain.plaintext);
        CHECK(result.magicStr.empty());
        CHECK(result.magicInt == 0U);
    }

    SECTION("a corrupted sample fails the integer candidate attempt")
    {
        file_fixture broken = make_custom_file();
        broken.encrypted[static_cast<std::size_t>(broken.fixture.samples[0])] ^= 0x01U;
        wolfx::DecryptResult result;
        result.decData.assign(broken.encrypted.size(), 0);
        std::copy(broken.encrypted.begin(), broken.encrypted.begin() + 10, result.decData.begin());
        CHECK_FALSE(wolfx::detail::crack::tryDecryptP1(broken.encrypted, key, full, result));
    }

    SECTION("a key whose payload offset does not fit the buffer cannot be prepared")
    {
        const wolfx::WolfXData tiny(5, 0);
        wolfx::DecryptResult result;
        result.decData.assign(5, 0);
        CHECK_FALSE(wolfx::detail::crack::tryDecryptP1(tiny, key, full, result));
    }
}

// The frozen whole-buffer oracle still answers for the same fixtures, including the failure
// contract the streaming backend now shares.
TEST_CASE("WolfX frozen whole-buffer oracle keeps its contract", "[wolfrpg][wolfx]")
{
    SECTION("a wrong magic string fails the oracle")
    {
        const file_fixture custom = make_custom_file();
        wolfx::WolfXData dec(custom.encrypted.size());
        std::uint32_t offset = 0;
        CHECK_FALSE(wolfx::detail::crack::decryptFull(custom.encrypted, std::string("kek"), std::string("WRONG"),
                                                      0x12345678U, dec, offset));
    }

    SECTION("the default fixture takes the zero-integer branch")
    {
        const file_fixture plain = make_default_file();
        wolfx::WolfXData dec(plain.encrypted.size());
        std::copy(plain.encrypted.begin(), plain.encrypted.begin() + 10, dec.begin());
        std::uint32_t offset = 0;
        REQUIRE(wolfx::detail::crack::decryptFull(plain.encrypted, std::string(), std::string(), 0U, dec, offset));
        CHECK(offset == plain.fixture.payload_offset);
        CHECK(dec == plain.plaintext);
    }

    SECTION("a buffer smaller than the payload offset fails the oracle")
    {
        const wolfx::WolfXData tiny(20, 0);
        wolfx::WolfXData dec(20, 0);
        std::uint32_t offset = 0;
        CHECK_FALSE(wolfx::detail::crack::decryptFull(tiny, std::string("kek"), std::string("MAGICKEY"), 0x12345678U,
                                                      dec, offset));
    }
}

// The sample/checksum bounds and the scalar XOR are shared; these cases pin the two boundary
// behaviours the streaming-only tests never reach.
TEST_CASE("WolfX shared helpers reject an impossible payload and agree on the XOR", "[wolfrpg][wolfx]")
{
    SECTION("a payload offset at or past the physical size cannot be sampled")
    {
        const wolfx::DecryptBlob blob{};
        const std::array<std::uint8_t, 5> checksum{};
        CHECK_FALSE(wolfx::detail::crack::validateSamples(blob, 600, 512, checksum,
                                                          [](const std::uint64_t) { return std::uint8_t{0}; }));
    }

    SECTION("the buffer and range XOR paths produce the same bytes")
    {
        const file_fixture file = make_custom_file();
        wolfx::WolfXData whole(file.encrypted.size(), 0);
        std::copy(file.encrypted.begin(), file.encrypted.begin() + 15, whole.begin());
        wolfx::detail::dataManip::xorBufferBlobPlain(file.encrypted, file.fixture.blob, whole);

        const std::size_t payload = static_cast<std::size_t>(file.fixture.payload_offset);
        std::vector<std::uint8_t> range(file.encrypted.size() - payload);
        wolfx::stream::xorRange(file.encrypted.data() + payload, range.size(), file.fixture.payload_offset,
                                file.fixture.blob, range.data());

        for (std::size_t i = 0; i < range.size(); ++i) {
            CHECK(range[i] == whole[payload + i]);
            CHECK(range[i] == file.plaintext[payload + i]);
        }
    }
}

TEST_CASE("unified WolfX facade bounds every real write and progress delta to 32KiB", "[recovery-core-review]")
{
    const auto file = make_default_file(100000);
    auto source = open_stream(file.encrypted);
    const auto reader = unpackrat::wolfrpg::archive::open(source, "owned.wolfx");
    struct destination final : std::stringbuf
    {
        std::vector<std::streamsize> writes;
        std::streamsize xsputn(const char *bytes, const std::streamsize size) override
        {
            writes.push_back(size);
            return std::stringbuf::xsputn(bytes, size);
        }
    } buffer;
    std::ostream output(&buffer);
    std::vector<std::int64_t> progress;
    reader->extract(0, source, output, [&](const auto delta) { progress.push_back(delta); });
    REQUIRE(buffer.writes.size() == progress.size());
    std::int64_t total = 0;
    for (std::size_t i = 0; i < progress.size(); ++i) {
        CHECK(buffer.writes[i] <= 32768);
        CHECK(progress[i] <= 32768);
        CHECK(progress[i] == buffer.writes[i]);
        total += progress[i];
    }
    CHECK(total == 100000);
    REQUIRE(file.fixture.payload_offset <= file.plaintext.size());
    const auto offset = static_cast<std::size_t>(file.fixture.payload_offset);
    CHECK(buffer.str() ==
          std::string(reinterpret_cast<const char *>(file.plaintext.data() + offset), file.plaintext.size() - offset));
}

#include "../../core/wolfrpg/game_context.h"
unpackrat::wolfrpg::game_context_result recovery_context_fault(const std::filesystem::path &path);

TEST_CASE("WolfX recovery preserves a context probe IO failure as read_error", "[recovery-core-review]")
{
    const auto file = make_custom_file();
    auto source = open_stream(file.encrypted);
    bool failed = false;
    try {
        unpackrat::wolfrpg::archive::open(source, "owned.wolfx", {}, recovery_context_fault);
    } catch (const read_error &) {
        failed = true;
    }
    CHECK(failed);
}

TEST_CASE("owned WolfX byte sources peek without consuming and honor their readable extent",
          "[wolfrpg][wolfx][source-helper]")
{
    SECTION("generated source")
    {
        generated_streambuf buffer(3, [](std::uint64_t at) { return static_cast<std::uint8_t>('A' + at); });
        std::istream stream(&buffer);
        REQUIRE(stream.peek() == 'A');
        REQUIRE(stream.peek() == 'A');
        CHECK(stream.get() == 'A');
        CHECK(stream.get() == 'B');
        CHECK(stream.get() == 'C');
        CHECK(stream.peek() == std::char_traits<char>::eof());
    }
    SECTION("bounded generated source")
    {
        generated_streambuf buffer(3, [](std::uint64_t at) { return static_cast<std::uint8_t>('A' + at); });
        buffer.set_serve_limit(1);
        std::istream stream(&buffer);
        REQUIRE(stream.peek() == 'A');
        REQUIRE(stream.peek() == 'A');
        CHECK(stream.get() == 'A');
        CHECK(stream.peek() == std::char_traits<char>::eof());
    }
    SECTION("owned fault source")
    {
        faulting_source_streambuf buffer({'A', 'B', 'C'});
        std::istream stream(&buffer);
        REQUIRE(stream.peek() == 'A');
        REQUIRE(stream.peek() == 'A');
        CHECK(stream.get() == 'A');
        CHECK(stream.get() == 'B');
        CHECK(stream.get() == 'C');
        CHECK(stream.peek() == std::char_traits<char>::eof());
    }
}

TEST_CASE("WolfX sparse source returns exact owned bytes and zero for absent positions",
          "[wolfrpg][wolfx][source-helper]")
{
    const std::map<std::uint64_t, std::uint8_t> source{{1, 7}, {2, 0}};
    CHECK(sparse_byte(source, 1) == 7);
    CHECK(sparse_byte(source, 2) == 0);
    CHECK(sparse_byte(source, 3) == 0);
    CHECK(source.size() == 2);
}
TEST_CASE("WolfX typed exception observation accepts a successful real archive operation",
          "[wolfrpg][wolfx][source-helper]")
{
    const auto file = make_default_file();
    std::istringstream stream = open_stream(file.encrypted);
    const auto failure = observed_exception<read_error>([&] {
        const auto source = wolfx_archive::open(stream, "owned.wolfx", file.candidates);
        REQUIRE(source);
        CHECK_FALSE(source->entries().empty());
    });
    CHECK_FALSE(failure.has_value());
}

TEST_CASE("WolfX owned stream helper public seeks and reads preserve position on rejection",
          "[wolfrpg][wolfx][source-helper]")
{
    const auto exercise = [](std::streambuf &buffer) {
        char byte = '!';
        CHECK(buffer.sgetn(&byte, 0) == 0);
        CHECK(byte == '!');
        CHECK(buffer.pubseekoff(1, std::ios::beg, std::ios::in) == std::streampos(1));
        CHECK(buffer.pubseekoff(0, std::ios::cur, std::ios::in) == std::streampos(1));
        CHECK(buffer.pubseekoff(-2, std::ios::beg, std::ios::in) == std::streampos(-1));
        CHECK(buffer.pubseekoff(1, std::ios::end, std::ios::in) == std::streampos(-1));
        CHECK(buffer.pubseekoff(0, std::ios::cur, std::ios::in) == std::streampos(1));
        CHECK(buffer.pubseekpos(0, std::ios::in) == std::streampos(0));
        std::array<char, 3> bytes{};
        CHECK(buffer.sgetn(bytes.data(), 3) == 3);
        CHECK(bytes == std::array<char, 3>{'A', 'B', 'C'});
        CHECK(buffer.sgetc() == std::char_traits<char>::eof());
        CHECK(buffer.sbumpc() == std::char_traits<char>::eof());
        CHECK(buffer.pubseekoff(-1, std::ios::cur, std::ios::in) == std::streampos(2));
        CHECK(buffer.sgetc() == 'C');
        CHECK(buffer.sgetc() == 'C');
    };
    generated_streambuf generated(3, [](std::uint64_t at) { return static_cast<std::uint8_t>('A' + at); });
    faulting_source_streambuf owned({'A', 'B', 'C'});
    exercise(generated);
    exercise(owned);
}
TEST_CASE("WolfX owned exceptional stream helpers preserve scalar and absolute operation contracts",
          "[wolfrpg][wolfx][source-helper]")
{
    throwing_input_streambuf input;
    CHECK(input.pubseekpos(0, std::ios::in) == std::streampos(0));
    const auto input_error = observed_exception<io_sentinel>([&] { (void)input.sgetc(); });
    REQUIRE(input_error.has_value());
    if (!input_error.has_value()) {
        return;
    }
    CHECK(input_error.value().code == 11);
    CHECK(input_error.value().payload == "input");
    throwing_output_streambuf output_buffer;
    std::ostream output(&output_buffer);
    output.exceptions(std::ios::badbit);
    const auto output_error = observed_exception<io_sentinel>([&] { output.put('X'); });
    REQUIRE(output_error.has_value());
    if (!output_error.has_value()) {
        return;
    }
    CHECK(output_error.value().code == 22);
    CHECK(output_error.value().payload == "output");
    CHECK(output.bad());
    failing_streambuf rejected;
    std::ostream failed(&rejected);
    failed.put('X');
    CHECK(failed.bad());
    std::size_t calls = 0;
    exception_source_streambuf quiet([&] { ++calls; });
    char byte = '!';
    CHECK(quiet.sgetn(&byte, 1) == 0);
    CHECK(byte == '!');
    CHECK(quiet.sgetc() == std::char_traits<char>::eof());
    CHECK(quiet.pubseekoff(0, std::ios::beg, std::ios::in) == std::streampos(-1));
    CHECK(quiet.pubseekpos(0, std::ios::in) == std::streampos(-1));
    CHECK(calls == 4);
    exception_source_streambuf throwing([] {
        throw io_sentinel{33, "source"}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
    });
    const std::array<std::function<void()>, 3> operations{[&] { (void)throwing.sgetn(&byte, 1); },
                                                          [&] { (void)throwing.sgetc(); },
                                                          [&] { (void)throwing.pubseekpos(0, std::ios::in); }};
    for (const auto &operation : operations) {
        const auto error = observed_exception<io_sentinel>(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(error.value().code == 33);
        CHECK(error.value().payload == "source");
    }
}
