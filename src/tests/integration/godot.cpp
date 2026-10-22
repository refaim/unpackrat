// Actual-DLL integration tests for the Godot module, driven through the shared
// ``test::observer`` framework exactly like the other archive adapters.
//
// This translation unit belongs in ``tests.exe``: it consumes only the public ``test::observer``
// interface and defines no extractor or ABI symbols of its own, so linking it beside the
// framework's observer.cpp introduces no duplicate symbols.
//
// Every PCK build below is repository-owned and generated in memory. A single fixture exercises
// the three located containers (standalone, PE section, trailing footer), the presentation tree
// (decoded substitutions with raw fallbacks), import/remap path restoration, the generated name
// manifest, packed presentation sizes, and byte-exact decoded outputs.
//
#include "../framework/observer.h"

#include "../../core/codecs/crypto_codec.h"
#include "../support/archive_fixtures.h"
#include "../support/godot_fixtures.h"
#include "../support/godot_resource_fixtures.h"
#include "../support/godot_texture_fixtures.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

#define SPNG_STATIC
#include <spng.h>

#define WEBP_STATIC
#include <webp/decode.h>

#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>
#include <windows.h>

namespace
{
    using test::support::build_gdst;
    using test::support::build_godot_pck;
    using test::support::build_resource;
    using test::support::gdst_fixture;
    using test::support::godot_bytes;
    using test::support::godot_entry_options;
    using test::support::godot_pck_options;
    using test::support::godot_resource_bytes;
    using test::support::png_bytes;
    using test::support::resource_document_spec;
    using test::support::resource_property_spec;
    using test::support::resource_record_spec;
    using test::support::temporary_archive;
    using test::support::variant_encoder;
    using test::support::wire_writer;

    // The ABI presents paths with the host separator; normalize so a case can match either style.
    std::string normalize(const std::wstring &path)
    {
        std::string result;
        result.reserve(path.size());
        for (const wchar_t character : path) {
            const wchar_t value = character == L'\\' ? L'/' : character;
            result.push_back(static_cast<char>(static_cast<unsigned>(value) & 0xFFU));
        }
        return result;
    }

    // Exact, normalized lookup keeps distinct siblings and renamed collision entries separate.
    const test::file *find_file(const std::vector<test::file> &files, const std::string_view path)
    {
        for (const test::file &file : files) {
            if (normalize(file.path) == path) {
                return &file;
            }
        }
        return nullptr;
    }

    std::string as_string(const std::vector<std::uint8_t> &bytes)
    {
        return {bytes.begin(), bytes.end()};
    }

    // The exact ".import" metadata stored for ``res://art/hero.png`` remains directly reachable.
    constexpr std::string_view import_metadata = "[remap]\n"
                                                 "importer=\"texture\"\n"
                                                 "path=\"res://.godot/imported/hero-h.ctex\"\n"
                                                 "[deps]\n"
                                                 "source_file=\"res://art/hero.png\"\n";

    std::vector<std::string> tab_fields(const std::string_view row)
    {
        std::vector<std::string> fields;
        std::string current;
        for (const char character : row) {
            if (character == '\t') {
                fields.push_back(std::move(current));
                current.clear();
                continue;
            }
            current.push_back(character);
        }
        fields.push_back(std::move(current));
        return fields;
    }

    int hex_value(const char digit)
    {
        if (digit >= '0' && digit <= '9') {
            return digit - '0';
        }
        if (digit >= 'A' && digit <= 'F') {
            return digit - 'A' + 10;
        }
        return -1;
    }

    // Reverses the manifest's per-field percent escaping so the original byte name is recoverable.
    std::string unescape_field(const std::string_view field)
    {
        std::string out;
        for (std::size_t index = 0; index < field.size(); ++index) {
            if (field[index] == '%' && index + 2 < field.size()) {
                const int high = hex_value(field[index + 1]);
                const int low = hex_value(field[index + 2]);
                if (high >= 0 && low >= 0) {
                    out.push_back(static_cast<char>((static_cast<unsigned>(high) << 4U) | static_cast<unsigned>(low)));
                    index += 2;
                    continue;
                }
            }
            out.push_back(field[index]);
        }
        return out;
    }

    std::string read_back(const std::filesystem::path &path)
    {
        std::ifstream input(path, std::ios::binary);
        REQUIRE(input.is_open());
        return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    }

    std::string extract_bytes(const test::file &file, const std::filesystem::path &destination)
    {
        std::error_code error;
        std::filesystem::remove(destination, error);
        file.extract(destination);
        const std::string contents = read_back(destination);
        std::filesystem::remove(destination, error);
        return contents;
    }

    std::vector<std::byte> decode_png_rgba(const std::string_view encoded)
    {
        spng_ctx *context = spng_ctx_new(0);
        REQUIRE(context != nullptr);
        const auto release = std::unique_ptr<spng_ctx, decltype(&spng_ctx_free)>(context, spng_ctx_free);

        REQUIRE(spng_set_png_buffer(context, encoded.data(), encoded.size()) == SPNG_OK);
        spng_ihdr header{};
        REQUIRE(spng_get_ihdr(context, &header) == SPNG_OK);
        REQUIRE(header.width == 1);
        REQUIRE(header.height == 1);

        std::size_t decoded_size = 0;
        REQUIRE(spng_decoded_image_size(context, SPNG_FMT_RGBA8, &decoded_size) == SPNG_OK);
        std::vector<std::byte> pixels(decoded_size);
        REQUIRE(spng_decode_image(context, pixels.data(), pixels.size(), SPNG_FMT_RGBA8, 0) == SPNG_OK);
        return pixels;
    }

    std::vector<std::byte> decode_webp_rgba(const std::string_view encoded)
    {
        int width = 0;
        int height = 0;
        const auto *bytes = reinterpret_cast<const std::uint8_t *>(encoded.data());
        REQUIRE(WebPGetInfo(bytes, encoded.size(), &width, &height) != 0);
        REQUIRE(width == 1);
        REQUIRE(height == 1);

        std::vector<std::uint8_t> rgba(static_cast<std::size_t>(width) * height * 4);
        REQUIRE(WebPDecodeRGBAInto(bytes, encoded.size(), rgba.data(), rgba.size(), width * 4) != nullptr);
        std::vector<std::byte> pixels;
        pixels.reserve(rgba.size());
        for (const std::uint8_t value : rgba) {
            pixels.push_back(static_cast<std::byte>(value));
        }
        return pixels;
    }

    // Holding an exclusive byte-range lock over a stored payload makes any listing-time body read
    // fail deterministically. The pack locator may still read the leading analysis prefix, so the
    // cases below place their target bodies beyond that prefix.
    struct handle_closer final
    {
        void operator()(void *handle) const noexcept
        {
            static_cast<void>(CloseHandle(handle));
        }
    };

    class locked_file_range final
    {
      public:
        locked_file_range(const std::filesystem::path &path, const std::uint64_t offset, const std::uint64_t length)
        {
            if (length == 0 || length > MAXDWORD) {
                throw std::invalid_argument("Invalid range-lock length");
            }
            const HANDLE opened =
                CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (opened == INVALID_HANDLE_VALUE) {
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                        "Failed to open file for a range lock");
            }
            file_.reset(opened);
            overlapped_.Offset = static_cast<DWORD>(offset);
            overlapped_.OffsetHigh = static_cast<DWORD>(offset >> 32U);
            length_ = static_cast<DWORD>(length);
            if (!LockFileEx(file_.get(), LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, 0, length_, 0,
                            &overlapped_)) {
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                        "Failed to lock file range");
            }
        }

        ~locked_file_range()
        {
            if (file_ != nullptr) {
                static_cast<void>(UnlockFileEx(file_.get(), 0, length_, 0, &overlapped_));
            }
        }

        locked_file_range(const locked_file_range &) = delete;
        locked_file_range &operator=(const locked_file_range &) = delete;

      private:
        std::unique_ptr<void, handle_closer> file_;
        OVERLAPPED overlapped_{};
        DWORD length_ = 0;
    };

    std::uint64_t payload_offset(const godot_bytes &packed, const godot_bytes &payload)
    {
        REQUIRE(!payload.empty());
        const auto found = std::search(packed.begin(), packed.end(), payload.begin(), payload.end());
        REQUIRE(found != packed.end());
        return static_cast<std::uint64_t>(std::distance(packed.begin(), found));
    }

    std::string md5_hex(const std::string_view text)
    {
        unpackrat::codecs::md5_state hash;
        hash.update(std::span<const std::byte>(reinterpret_cast<const std::byte *>(text.data()), text.size()));
        const auto digest = hash.finish();
        static constexpr char digits[] = "0123456789abcdef";
        std::string output;
        output.reserve(digest.size() * 2);
        for (const std::byte value : digest) {
            const auto byte = std::to_integer<unsigned>(value);
            output.push_back(digits[byte >> 4U]);
            output.push_back(digits[byte & 0x0FU]);
        }
        return output;
    }

    godot_entry_options entry(std::string path, std::vector<std::uint8_t> payload)
    {
        godot_entry_options options;
        options.path = std::move(path);
        options.payload = std::move(payload);
        return options;
    }

    // ``godot_bytes`` and ``godot_resource_bytes`` are the same vector alias, so one overload
    // serves every generated fixture blob.
    std::vector<std::uint8_t> as_u8(const std::vector<std::uint8_t> &bytes)
    {
        return bytes;
    }

    std::vector<std::uint8_t> as_u8(const std::string_view text)
    {
        return {text.begin(), text.end()};
    }

    // A Godot 3 StreamTexture carrying a preserved PNG base level, the compiled ".ctex" payload.
    godot_bytes compiled_texture()
    {
        gdst_fixture fixture;
        fixture.width = 8;
        fixture.height = 8;
        fixture.data_format = test::support::gdst_bit_png | test::support::gdst_bit_stream;
        fixture.mips = {png_bytes(32)};
        return build_gdst(fixture);
    }

    // A complete 1x1 red texture whose preserved PNG level can be decoded independently.
    godot_bytes compiled_red_texture(const std::uint32_t flags = 0)
    {
        gdst_fixture fixture;
        fixture.width = 1;
        fixture.height = 1;
        fixture.flags = flags;
        fixture.data_format = test::support::gdst_bit_png | test::support::gdst_bit_stream;
        fixture.mips = {test::support::red_pixel_png()};
        return build_gdst(fixture);
    }

    // A Godot 3 texture whose preserved media is WebP even though an imported source could have
    // been named ".png". The body must never be emitted under a ".png" destination.
    godot_bytes compiled_webp_texture()
    {
        godot_bytes wrapped = {'W', 'E', 'B', 'P'};
        const godot_bytes riff = test::support::webp_riff_payload();
        wrapped.insert(wrapped.end(), riff.begin(), riff.end());
        gdst_fixture fixture;
        fixture.width = 8;
        fixture.height = 8;
        fixture.data_format = test::support::gdst_bit_webp | test::support::gdst_bit_stream;
        fixture.mips = {wrapped};
        return build_gdst(fixture);
    }

    // A modelless binary resource whose root is not an image, so it always keeps its raw bytes.
    godot_resource_bytes plain_resource()
    {
        resource_document_spec spec;
        spec.engine_major = 4;
        spec.engine_minor = 5;
        spec.format_version = 6;
        spec.main_type = "Resource";
        resource_record_spec root;
        root.path = "res://root";
        root.type = "Resource";
        resource_property_spec name;
        name.name = "resource_name";
        wire_writer writer;
        variant_encoder encoder(writer, true, false);
        encoder.string("root");
        name.value = writer.take();
        root.properties.push_back(std::move(name));
        spec.records.push_back(std::move(root));
        return build_resource(spec);
    }

    // A PCK whose entries cover the script/metadata/unknown/compiled-image/escaped-name/duplicate
    // shapes. The escaped ``odd?.bin`` name forces the presentation tree to emit its name
    // manifest, which the manifest case below verifies semantically.
    godot_pck_options integration_pack()
    {
        godot_pck_options options;
        options.version = 4;
        options.engine_version = {4, 0, 0};
        options.entries.push_back(entry("res://script.gd", as_u8("extends Node\n")));
        options.entries.push_back(entry("res://data/unknown.bin", {0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01}));
        options.entries.push_back(entry("res://art/hero.png.import", as_u8(import_metadata)));
        options.entries.push_back(entry("res://.godot/imported/hero-h.ctex", as_u8(compiled_texture())));
        options.entries.push_back(entry("res://data/plain.res", as_u8(plain_resource())));
        options.entries.push_back(entry("res://assets/dup.bin", {0x11}));
        options.entries.push_back(entry("res://assets/dup.bin", {0x22}));
        options.entries.push_back(entry("res://data/odd?.bin", {0x33}));
        return options;
    }

    // A ".import"/".remap" fragment mapping one virtual path onto one compiled resource, in the
    // same shape the engine writes and the fixture pack above already uses.
    std::string remap_metadata(const std::string_view virtual_path, const std::string_view compiled_path)
    {
        std::string text = "[remap]\nimporter=\"texture\"\npath=\"";
        text += compiled_path;
        text += "\"\n[deps]\nsource_file=\"";
        text += virtual_path;
        text += "\"\n";
        return text;
    }

    // A ".import" fragment that amplifies: one source_file (the virtual path every mapping shares)
    // plus ``count`` distinct ``path.<variant>`` compiled keys. ``read_import_remap`` clones the
    // normalised source_file into every mapping, so a fragment of a few KiB retains roughly
    // ``count`` times the source_file length in aliases.
    std::string amplified_metadata(const std::string_view source_file, const std::size_t count)
    {
        std::string text = "[remap]\nimporter=\"texture\"\n";
        for (std::size_t index = 0; index < count; ++index) {
            text += "path.v";
            text += std::to_string(index);
            text += "=\"res://.godot/imported/amp";
            text += std::to_string(index);
            text += ".ctex\"\n";
        }
        text += "[deps]\nsource_file=\"";
        text += source_file;
        text += "\"\n";
        return text;
    }
} // namespace

TEST_CASE("godot integration: a standalone pack extracts every physical source verbatim", "[integration][godot]")
{
    const temporary_archive archive("godot-standalone", build_godot_pck(integration_pack()));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *script = find_file(files, "script.gd");
    REQUIRE(script != nullptr);
    CHECK(script->compressed_size == 13);
    CHECK(script->uncompressed_size == 13);
    CHECK(extract_bytes(*script, destination) == "extends Node\n");

    const test::file *unknown = find_file(files, "data/unknown.bin");
    REQUIRE(unknown != nullptr);
    CHECK(unknown->compressed_size == 6);
    CHECK(extract_bytes(*unknown, destination) == std::string("\xDE\xAD\xBE\xEF\x00\x01", 6));

    // The unmodelled resource keeps its compiled bytes as a source-backed entry.
    const test::file *plain = find_file(files, "data/plain.res");
    REQUIRE(plain != nullptr);
    CHECK(plain->compressed_size == static_cast<int64_t>(plain_resource().size()));
    CHECK(plain->uncompressed_size == plain->compressed_size);
    CHECK(extract_bytes(*plain, destination) == as_string(plain_resource()));

    // Collision-safe originals keep both duplicate sources reachable by their own index.
    const test::file *dup0 = find_file(files, "assets/dup.bin");
    const test::file *dup1 = find_file(files, "assets/dup~1.bin");
    REQUIRE(dup0 != nullptr);
    REQUIRE(dup1 != nullptr);
    CHECK(extract_bytes(*dup0, destination) == std::string("\x11", 1));
    CHECK(extract_bytes(*dup1, destination) == std::string("\x22", 1));
}

TEST_CASE("godot integration: the PE-section and trailing-footer containers behave identically", "[integration][godot]")
{
    const auto expect_script = [](const std::filesystem::path &path) {
        const test::observer plugin;
        const auto files = plugin.list_files(path);
        const test::file *script = find_file(files, "script.gd");
        REQUIRE(script != nullptr);
        CHECK(extract_bytes(*script, path.wstring() + L".out") == "extends Node\n");
    };

    const temporary_archive pe("godot-pe", test::support::wrap_godot_pck_in_pe(integration_pack()));
    expect_script(pe.path());

    const temporary_archive footer("godot-footer", test::support::wrap_godot_pck_in_footer(integration_pack(), 0x40));
    expect_script(footer.path());
}

TEST_CASE("godot integration: an import alias substitutes its compiled source", "[integration][godot]")
{
    const temporary_archive archive("godot-alias", build_godot_pck(integration_pack()));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The remap metadata remains in its original folder and extracts byte for byte.
    CHECK(find_file(files, "art/hero.png.import") != nullptr);
    const test::file *metadata = find_file(files, "art/hero.png.import");
    REQUIRE(metadata != nullptr);
    CHECK(extract_bytes(*metadata, destination) == std::string(import_metadata));

    // Decoding substitutes the compiled resource. PackedSize still describes its source envelope.
    const godot_bytes compiled_source = compiled_texture();
    CHECK(find_file(files, "art/hero.ctex") == nullptr);

    const test::file *alias = find_file(files, "art/hero.png");
    REQUIRE(alias != nullptr);
    CHECK(alias->compressed_size == static_cast<int64_t>(compiled_source.size()));
    CHECK(alias->uncompressed_size == alias->compressed_size);

    // The converted view is the preserved PNG level of the compiled texture, so the full actual PNG
    // bytes must match rather than only the 8-byte signature.
    const std::string converted = extract_bytes(*alias, destination);
    CHECK(converted == as_string(png_bytes(32)));
}

TEST_CASE("godot integration: exported imports restore folders and preserve terminal NUL bytes", "[integration][godot]")
{
    godot_pck_options options;
    options.version = 2;
    options.engine_version = {4, 2, 1};
    std::string metadata = "[remap]\n\nimporter=\"texture\"\n"
                           "path=\"res://.godot/imported/hero-h.ctex\"\n"
                           "metadata={\n\"vram_texture\": false\n}\n";
    metadata.push_back('\0');
    const godot_bytes compiled_source = compiled_texture();
    options.entries.push_back(entry("res://art/hero.png.import", as_u8(metadata)));
    options.entries.push_back(entry("res://.godot/imported/hero-h.ctex", as_u8(compiled_source)));
    const temporary_archive archive("godot-exported-import", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *alias = find_file(files, "art/hero.png");
    REQUIRE(alias != nullptr);
    CHECK(extract_bytes(*alias, destination) == as_string(png_bytes(32)));
    CHECK(find_file(files, ".godot/imported/hero-h.png") == nullptr);
    CHECK(find_file(files, "art/hero.png.import") != nullptr);

    const test::file *original_metadata = find_file(files, "art/hero.png.import");
    REQUIRE(original_metadata != nullptr);
    CHECK(extract_bytes(*original_metadata, destination) == metadata);
    CHECK(find_file(files, "art/hero.ctex") == nullptr);
    CHECK(alias->compressed_size == static_cast<int64_t>(compiled_source.size()));
}

TEST_CASE("godot integration: aliases keep their own output encoding and cannot hide a later support",
          "[integration][godot][alias-encoding]")
{
    const std::string hash = "00112233445566778899aabbccddeeff";
    const std::string compiled = "res://.godot/imported/shared.png-" + hash + ".ctex";
    const godot_bytes texture = compiled_red_texture();

    SECTION("PNG and WebP aliases decode independently from one physical texture")
    {
        godot_pck_options options;
        options.version = 4;
        options.engine_version = {4, 0, 0};
        options.entries.push_back(entry("res://art/a.png.import", as_u8(remap_metadata("res://art/a.png", compiled))));
        options.entries.push_back(
            entry("res://art/b.webp.import", as_u8(remap_metadata("res://art/b.webp", compiled))));
        options.entries.push_back(entry(compiled, texture));

        const temporary_archive archive("godot-alias-encodings", build_godot_pck(options));
        const test::observer plugin;
        const auto files = plugin.list_files(archive.path());
        const auto destination = archive.path().wstring() + L".out";

        const test::file *png = find_file(files, "art/a.png");
        const test::file *webp = find_file(files, "art/b.webp");
        REQUIRE(png != nullptr);
        REQUIRE(webp != nullptr);

        const std::string png_bytes = extract_bytes(*png, destination);
        const std::string webp_bytes = extract_bytes(*webp, destination);
        REQUIRE(png_bytes.starts_with(std::string_view("\x89PNG\r\n\x1A\n", 8)));
        REQUIRE(webp_bytes.starts_with("RIFF"));
        REQUIRE(webp_bytes.substr(8, 4) == "WEBP");
        CHECK(decode_png_rgba(png_bytes) ==
              std::vector<std::byte>{std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255}});
        CHECK(decode_webp_rgba(webp_bytes) ==
              std::vector<std::byte>{std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255}});
    }

    SECTION("an unsupported first alias does not suppress a supported later alias")
    {
        godot_pck_options options;
        options.version = 4;
        options.engine_version = {4, 0, 0};
        options.entries.push_back(
            entry("res://art/first.gif.import", as_u8(remap_metadata("res://art/first.gif", compiled))));
        options.entries.push_back(
            entry("res://art/second.png.import", as_u8(remap_metadata("res://art/second.png", compiled))));
        options.entries.push_back(entry(compiled, texture));

        const temporary_archive archive("godot-alias-later-support", build_godot_pck(options));
        const test::observer plugin;
        const auto files = plugin.list_files(archive.path());

        const test::file *raw = find_file(files, "art/first.ctex");
        REQUIRE(raw != nullptr);
        CHECK(extract_bytes(*raw, archive.path().wstring() + L".out") == as_string(texture));

        const test::file *supported = find_file(files, "art/second.png");
        REQUIRE(supported != nullptr);
        const std::string output = extract_bytes(*supported, archive.path().wstring() + L".out");
        CHECK(decode_png_rgba(output) ==
              std::vector<std::byte>{std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255}});
    }
}

TEST_CASE("godot integration: a failed range-lock constructor closes its handle",
          "[integration][godot][locked-range-raii]")
{
    const temporary_archive archive("godot-lock-raii", godot_bytes(16, 0x5A));
    const std::filesystem::path &path = archive.path();
    {
        const locked_file_range first_lock(path, 0, 1);
        CHECK_THROWS_AS(locked_file_range(path, 0, 1), std::system_error);
        CHECK_THROWS_AS(locked_file_range(path.wstring() + L".missing", 0, 1), std::system_error);
    }

    // An exclusive reopen proves the failed constructor released the helper's read handle.
    const HANDLE opened =
        CreateFileW(path.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    const std::unique_ptr<void, handle_closer> exclusive(opened == INVALID_HANDLE_VALUE ? nullptr : opened);
    REQUIRE(exclusive != nullptr);
}

TEST_CASE("godot integration: conventional feature variants list without reading metadata or media bodies",
          "[integration][godot][filename-extension][variant-toc]")
{
    const std::string source_path = "res://art/picture.png";
    const std::string hash = md5_hex(source_path);
    const std::string metadata = remap_metadata(source_path, ".godot/imported/picture.png-" + hash + ".s3tc.ctex");
    const godot_bytes s3tc = compiled_red_texture(1);
    const godot_bytes etc2 = compiled_red_texture(2);

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://padding/keep.bin", godot_bytes(std::size_t{160} * 1024, 0x5A)));
    options.entries.push_back(entry("res://art/picture.png.import", as_u8(metadata)));
    options.entries.push_back(entry("res://.godot/imported/picture.png-" + hash + ".s3tc.ctex", s3tc));
    options.entries.push_back(entry("res://.godot/imported/picture.png-" + hash + ".etc2.ctex", etc2));

    const godot_bytes packed = build_godot_pck(options);
    const std::uint64_t metadata_offset = payload_offset(packed, as_u8(metadata));
    const std::uint64_t s3tc_offset = payload_offset(packed, s3tc);
    const std::uint64_t etc2_offset = payload_offset(packed, etc2);
    REQUIRE(metadata_offset > std::uint64_t{128} * 1024);
    REQUIRE(s3tc_offset > std::uint64_t{128} * 1024);
    REQUIRE(etc2_offset > std::uint64_t{128} * 1024);

    const temporary_archive archive("godot-variant-toc", packed);
    const test::observer plugin;
    const auto files = [&] {
        const locked_file_range metadata_lock(archive.path(), metadata_offset, metadata.size());
        const locked_file_range s3tc_lock(archive.path(), s3tc_offset, s3tc.size());
        const locked_file_range etc2_lock(archive.path(), etc2_offset, etc2.size());
        return plugin.list_files(archive.path());
    }();

    const test::file *s3tc_view = find_file(files, "art/picture.s3tc.png");
    const test::file *etc2_view = find_file(files, "art/picture.etc2.png");
    REQUIRE(s3tc_view != nullptr);
    REQUIRE(etc2_view != nullptr);
    CHECK(s3tc_view->compressed_size == static_cast<int64_t>(s3tc.size()));
    CHECK(etc2_view->compressed_size == static_cast<int64_t>(etc2.size()));
}

TEST_CASE("godot integration: a duplicate feature token keeps the conventional match conservative",
          "[integration][godot][filename-extension][variant-ambiguous]")
{
    const std::string source_path = "res://art/picture.png";
    const std::string hash = md5_hex(source_path);
    const std::string duplicate = "res://.godot/imported/picture.png-" + hash + ".s3tc.ctex";
    const std::string chosen = "res://.godot/imported/chosen.ctex";
    const std::string metadata = remap_metadata(source_path, chosen);
    const godot_bytes texture = compiled_red_texture();

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://art/picture.png.import", as_u8(metadata)));
    options.entries.push_back(entry(duplicate, texture));
    options.entries.push_back(entry(duplicate, texture));
    options.entries.push_back(entry(chosen, texture));

    const temporary_archive archive("godot-variant-ambiguous", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());

    const test::file *mapped = find_file(files, "art/picture.png");
    REQUIRE(mapped != nullptr);
    const std::string output = extract_bytes(*mapped, archive.path().wstring() + L".out");
    CHECK(decode_png_rgba(output) ==
          std::vector<std::byte>{std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255}});
    CHECK(find_file(files, "art/picture.s3tc.png") == nullptr);
}

TEST_CASE("godot integration: an oversized conventional variant group preserves every raw entry",
          "[integration][godot][filename-extension][variant-limit]")
{
    constexpr std::size_t variant_count = 65;
    const std::string source_path = "res://art/" + std::string(180, 'a') + "/picture.png";
    const std::string hash = md5_hex(source_path);
    const std::string metadata = remap_metadata(source_path, "res://.godot/imported/picture.png-" + hash + ".v0.ctex");

    std::vector<godot_bytes> textures;
    textures.reserve(variant_count);
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://padding/keep.bin", godot_bytes(std::size_t{160} * 1024, 0x5A)));
    options.entries.push_back(entry(source_path + ".import", as_u8(metadata)));
    for (std::size_t index = 0; index < variant_count; ++index) {
        CAPTURE(index);
        textures.push_back(compiled_red_texture(static_cast<std::uint32_t>(index + 1)));
        options.entries.push_back(entry(
            "res://.godot/imported/picture.png-" + hash + ".v" + std::to_string(index) + ".ctex", textures.back()));
    }

    const godot_bytes packed = build_godot_pck(options);
    const std::uint64_t metadata_offset = payload_offset(packed, as_u8(metadata));
    REQUIRE(metadata_offset > std::uint64_t{128} * 1024);

    const temporary_archive archive("godot-variant-limit", packed);
    const test::observer plugin;
    const auto files = [&] {
        const locked_file_range metadata_lock(archive.path(), metadata_offset, metadata.size());
        return plugin.list_files(archive.path());
    }();

    const std::string source_relative = source_path.substr(6);
    const std::string source_stem = source_relative.substr(0, source_relative.size() - 4);
    CHECK(find_file(files, source_stem + ".v0.png") == nullptr);

    const auto destination = archive.path().wstring() + L".out";
    for (std::size_t index = 0; index < variant_count; ++index) {
        CAPTURE(index);
        const std::string stored = ".godot/imported/picture.png-" + hash + ".v" + std::to_string(index) + ".ctex";
        const test::file *raw = find_file(files, stored);
        REQUIRE(raw != nullptr);
        CHECK(raw->compressed_size == static_cast<int64_t>(textures[index].size()));
        CHECK(extract_bytes(*raw, destination) == as_string(textures[index]));
    }
}

TEST_CASE("godot integration: group invalidation covers duplicate members at the variant boundary",
          "[integration][godot][filename-extension][variant-combination]")
{
    const std::string source_path = "res://art/" + std::string(180, 'a') + "/picture.png";
    const std::string hash = md5_hex(source_path);
    const std::string metadata = remap_metadata(source_path, "res://.godot/imported/picture.png-" + hash + ".v0.ctex");
    const std::string source_relative = source_path.substr(6);
    const std::string source_stem = source_relative.substr(0, source_relative.size() - 4);

    const auto run_group = [&](const std::string_view label, const std::size_t unique_count, const bool duplicate_first,
                               auto &&verify) {
        CAPTURE(label);
        struct stored_texture final
        {
            std::string path;
            godot_bytes bytes;
        };
        std::vector<stored_texture> textures;
        const auto add_variant = [&](const std::size_t index) {
            const std::string stored =
                "res://.godot/imported/picture.png-" + hash + ".v" + std::to_string(index) + ".ctex";
            textures.push_back(stored_texture{stored, compiled_red_texture(static_cast<std::uint32_t>(index + 1))});
        };
        if (duplicate_first) {
            add_variant(0);
            add_variant(0);
        }
        for (std::size_t index = duplicate_first ? 1 : 0; index < unique_count; ++index) {
            add_variant(index);
        }
        if (duplicate_first) {
            add_variant(1);
        }

        godot_pck_options options;
        options.version = 4;
        options.engine_version = {4, 0, 0};
        options.entries.push_back(entry("res://padding/keep.bin", godot_bytes(std::size_t{160} * 1024, 0x5A)));
        options.entries.push_back(entry(source_path + ".import", as_u8(metadata)));
        for (const stored_texture &texture : textures) {
            options.entries.push_back(entry(texture.path, texture.bytes));
        }

        const godot_bytes packed = build_godot_pck(options);
        const std::uint64_t metadata_offset = payload_offset(packed, as_u8(metadata));
        REQUIRE(metadata_offset > std::uint64_t{128} * 1024);
        const temporary_archive archive(label, packed);
        const test::observer plugin;
        const auto files = [&] {
            const locked_file_range metadata_lock(archive.path(), metadata_offset, metadata.size());
            return plugin.list_files(archive.path());
        }();
        verify(files, textures, archive.path().wstring() + L".out");
    };

    SECTION("64 unique variants remain eligible for filename inference")
    {
        run_group("godot-variant-limit-64", 64, false,
                  [&](const std::vector<test::file> &files, const auto &, const std::filesystem::path &) {
                      CHECK(find_file(files, source_stem + ".v0.png") != nullptr);
                      CHECK(find_file(files, ".godot/imported/picture.png-" + hash + ".v0.ctex") == nullptr);
                  });
    }

    SECTION("a duplicate before the 65th unique variant refuses every member of the group")
    {
        run_group(
            "godot-variant-duplicate-overflow", 65, true,
            [&](const std::vector<test::file> &files, const auto &textures, const std::filesystem::path &destination) {
                CHECK(find_file(files, source_stem + ".v0.png") == nullptr);
                for (std::size_t index = 0; index < 65; ++index) {
                    CHECK(find_file(files, source_stem + ".v" + std::to_string(index) + ".png") == nullptr);
                }
                for (std::size_t index = 0; index < textures.size(); ++index) {
                    CAPTURE(index);
                    std::string stored = textures[index].path.substr(6);
                    std::size_t duplicate = 0;
                    for (std::size_t previous = 0; previous < index; ++previous) {
                        if (textures[previous].path == textures[index].path) {
                            ++duplicate;
                        }
                    }
                    if (duplicate != 0) {
                        stored.insert(stored.size() - 5, "~" + std::to_string(duplicate));
                    }
                    const test::file *raw = find_file(files, stored);
                    REQUIRE(raw != nullptr);
                    CHECK(extract_bytes(*raw, destination) == as_string(textures[index].bytes));
                }
            });
    }
}

TEST_CASE("godot integration: a zero-byte physical source keeps its real empty size", "[integration][godot]")
{
    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry("res://empty.bin", std::vector<std::uint8_t>{}));
    options.entries.push_back(entry("res://keep.txt", as_u8("keep")));
    const temporary_archive archive("godot-empty", build_godot_pck(options));
    const auto destination = archive.path().wstring() + L".out";

    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());

    // A zero-byte source appears once, reports honest zero sizes and extracts an empty stream.
    const test::file *empty = find_file(files, "empty.bin");
    REQUIRE(empty != nullptr);
    CHECK(empty->compressed_size == 0);
    CHECK(empty->uncompressed_size == 0);
    CHECK(extract_bytes(*empty, destination).empty());

    CHECK(files.size() == 2);

    const test::file *kept = find_file(files, "keep.txt");
    REQUIRE(kept != nullptr);
    CHECK(extract_bytes(*kept, destination) == "keep");
}

TEST_CASE("godot integration: escaped names are recorded in the generated name manifest", "[integration][godot]")
{
    const temporary_archive archive("godot-manifest", build_godot_pck(integration_pack()));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The unsafe '?' in ``data/odd?.bin`` is escaped in the presented view, so it never surfaces as
    // the raw name, but the physical source stays reachable under its escaped name.
    CHECK(find_file(files, "data/odd?.bin") == nullptr);
    const test::file *escaped = find_file(files, "data/odd%3F.bin");
    REQUIRE(escaped != nullptr);
    CHECK(extract_bytes(*escaped, destination) == std::string("\x33", 1));

    // Duplicate physical names remain independently reachable by their own index.
    const test::file *dup0 = find_file(files, "assets/dup.bin");
    const test::file *dup1 = find_file(files, "assets/dup~1.bin");
    REQUIRE(dup0 != nullptr);
    REQUIRE(dup1 != nullptr);
    CHECK(extract_bytes(*dup0, destination) == std::string("\x11", 1));
    CHECK(extract_bytes(*dup1, destination) == std::string("\x22", 1));

    // The tree emits its name manifest, which records the original bytes of every renamed source
    // by raw index so nothing is lost. Assert the row bytes semantically: recover the original name
    // from the escaped field instead of pinning a byte-for-byte string.
    const test::file *manifest = find_file(files, "name-map.tsv");
    REQUIRE(manifest != nullptr);
    const std::string document = extract_bytes(*manifest, destination);
    REQUIRE(!document.empty());
    REQUIRE(document.ends_with('\n'));

    // The generated manifest is a generated metadata entry carrying its own byte counts rather than
    // a source-backed one, so the panel reports its own honest size: both the stored and the logical
    // size equal the generated text length.
    CHECK(manifest->compressed_size == static_cast<int64_t>(document.size()));
    CHECK(manifest->uncompressed_size == static_cast<int64_t>(document.size()));

    bool saw_escaped = false;
    bool saw_duplicate = false;
    for (std::size_t start = 0; start < document.size();) {
        const std::size_t end = document.find('\n', start);
        REQUIRE(end != std::string::npos);
        const std::vector<std::string> fields = tab_fields(std::string_view(document).substr(start, end - start));
        REQUIRE(fields.size() == 3);
        const std::string physical = unescape_field(fields[1]);
        CHECK(find_file(files, unescape_field(fields[2])) != nullptr);
        if (physical == "data/odd?.bin") {
            saw_escaped = true;
            CHECK(fields[0] == "7"); // the physical PCK index of ``res://data/odd?.bin``
        }
        if (physical == "assets/dup.bin" && fields[2] == "assets/dup~1.bin") {
            saw_duplicate = true;
            CHECK(fields[0] == "6"); // the second duplicate's physical PCK index
        }
        start = end + 1;
    }
    CHECK(saw_escaped);
    CHECK(saw_duplicate);
}

TEST_CASE("godot integration: metadata reads and aliases stay within one aggregate allowance", "[integration][godot]")
{
    // Each ".import"/".remap" fragment is already individually bounded (a 4 MiB body and at most
    // 128 mappings), so one listing must also bound their cumulative effect. Build one valid
    // alias, then enough 4 MiB fragments to exceed the shared 64 MiB allowance, then a further
    // valid alias that must be skipped once the allowance is exhausted. Every physical source
    // stays reachable raw regardless.
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};

    constexpr std::size_t metadata_body_bytes = std::size_t{4} * 1024 * 1024;
    constexpr std::size_t overwhelming_fragments = 17; // 17 * 4 MiB > the shared 64 MiB allowance

    options.entries.push_back(
        entry("res://art/a.png.import", as_u8(remap_metadata("res://art/a.png", "res://.godot/imported/a.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/a.ctex", as_u8(compiled_texture())));
    for (std::size_t index = 0; index < overwhelming_fragments; ++index) {
        options.entries.push_back(entry("res://junk/" + std::to_string(index) + ".import",
                                        std::vector<std::uint8_t>(metadata_body_bytes, 'x')));
    }
    options.entries.push_back(
        entry("res://art/z.png.import", as_u8(remap_metadata("res://art/z.png", "res://.godot/imported/z.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/z.ctex", as_u8(compiled_texture())));

    const temporary_archive archive("godot-metadata-bound", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The alias declared before the allowance is reached resolves normally.
    CHECK(find_file(files, "art/a.png") != nullptr);

    // The alias declared after the allowance is exhausted is skipped, so no unbounded alias set
    // is built. The generic compiled name carries no source suffix, so it remains a raw stored view.
    CHECK(find_file(files, "art/z.png") == nullptr);
    const godot_bytes compiled = compiled_texture();
    const test::file *z_original = find_file(files, ".godot/imported/z.ctex");
    REQUIRE(z_original != nullptr);
    CHECK(z_original->compressed_size == static_cast<int64_t>(compiled.size()));
    CHECK(extract_bytes(*z_original, destination) == as_string(compiled));

    // A skipped fragment's own physical source is still reachable raw.
    const test::file *junk_original = find_file(files, "junk/16.import");
    REQUIRE(junk_original != nullptr);
    CHECK(junk_original->compressed_size == static_cast<int64_t>(metadata_body_bytes));
}

TEST_CASE("godot integration: one aggregate alias allowance bounds amplified fragments", "[integration][godot]")
{
    // A single fragment is already individually bounded (at most 128 mappings), yet it still
    // amplifies: every one of its mappings clones the same normalised source_file, so a few KiB of
    // input retains roughly 128 times that source length in retained mapping strings. This builds
    // one real early compiled target, then enough amplifying fragments to exhaust the shared
    // aggregate alias allowance, then a late alias that must be skipped and late raw sources that
    // must stay reachable.
    //
    // The source path is long but legal: four 240-character components plus ``leaf.png`` keep every
    // component under the 255-char Windows limit and the whole normalised path under the 1023-char
    // cap. Each fragment's normalised source is ~978 bytes and is charged once per mapping (the
    // virtual tree charges every parsed mapping's virtual+compiled+variant length, whether or not
    // its compiled target exists), so 600 * 128 mappings of ~1070 bytes charge ~78.5 MiB of retained
    // metadata, past the 64 MiB allowance, while the physical fragment input is only a few MiB.
    //
    // Two target shapes are exercised: with only ``amp0`` present a fragment resolves a single
    // amplified alias, and with every one of the 128 compiled targets present each of its mappings
    // resolves, roughly tripling the retained tree cost (mapping text, resolved alias row and the
    // emitted entry). Both shapes must still list successfully and keep every raw source reachable.
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};

    const bool all_compiled_targets = GENERATE(false, true);
    constexpr std::size_t mappings_per_fragment = 128; // the parser's per-fragment mapping ceiling
    constexpr std::size_t amplified_fragments = 600;   // 600 * 128 * ~1070 B > the 64 MiB allowance
    const std::string long_component(240, 'a');

    const auto source_of = [&long_component](const std::size_t index) {
        const std::string stem = std::to_string(index);
        return "res://amp/" + stem + "/" + long_component + "/" + long_component + "/" + long_component + "/" +
               long_component + "/leaf.png";
    };

    // The real compiled targets. Each fragment's first mapping is its ``path.v0`` key, so ``amp0``
    // must resolve the first amplified alias when it is present. When every ``ampN.ctex`` is present
    // each of the 128 mappings per fragment resolves as well.
    if (all_compiled_targets) {
        for (std::size_t index = 0; index < mappings_per_fragment; ++index) {
            options.entries.push_back(
                entry("res://.godot/imported/amp" + std::to_string(index) + ".ctex", as_u8(compiled_texture())));
        }
    } else {
        options.entries.push_back(entry("res://.godot/imported/amp0.ctex", as_u8(compiled_texture())));
    }
    for (std::size_t index = 0; index < amplified_fragments; ++index) {
        options.entries.push_back(entry("res://amp/" + std::to_string(index) + ".import",
                                        as_u8(amplified_metadata(source_of(index), mappings_per_fragment))));
    }

    // A late alias declared after the amplifying fragments, then a late raw source after it.
    options.entries.push_back(
        entry("res://art/z.png.import", as_u8(remap_metadata("res://art/z.png", "res://.godot/imported/z.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/z.ctex", as_u8(compiled_texture())));
    const std::vector<std::uint8_t> late_raw = {0x51, 0x52, 0x53, 0x54, 0x55};
    options.entries.push_back(entry("res://late/keep.bin", late_raw));

    const temporary_archive archive("godot-alias-bound", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The first amplified mapping resolves: its alias is the first source stem plus the ``v0``
    // variant suffix plus the compiled texture's converted extension.
    const std::string early_alias =
        "amp/0/" + long_component + "/" + long_component + "/" + long_component + "/" + long_component + "/leaf.v0.png";
    CHECK(find_file(files, early_alias) != nullptr);
    // The late alias declared after the shared allowance is exhausted is skipped, so no unbounded
    // alias set is built.
    CHECK(find_file(files, "art/z.png") == nullptr);
    const test::file *late_texture = find_file(files, ".godot/imported/z.ctex");
    REQUIRE(late_texture != nullptr);
    CHECK(extract_bytes(*late_texture, destination) == as_string(compiled_texture()));
    // Every physical source still stays reachable: a late amplifying fragment's own metadata and
    // the late raw source both remain present, and the raw source extracts byte for byte.
    const test::file *late_fragment = find_file(files, "amp/599.import");
    REQUIRE(late_fragment != nullptr);
    const test::file *late_original = find_file(files, "late/keep.bin");
    REQUIRE(late_original != nullptr);
    CHECK(late_original->compressed_size == static_cast<int64_t>(late_raw.size()));
    CHECK(extract_bytes(*late_original, destination) == as_string(late_raw));
}

TEST_CASE("godot integration: conventional mappings stop before exceeding the residual alias allowance",
          "[integration][godot][filename-extension][conventional-budget]")
{
    constexpr std::size_t mappings_per_fragment = 128;
    constexpr std::size_t variants_per_group = 64;
    constexpr std::size_t conventional_groups = 8;
    constexpr std::size_t alias_allowance = std::size_t{32} * 1024 * 1024;
    constexpr std::size_t residual_target = std::size_t{128} * 1024;
    constexpr std::size_t variant_bytes = 10 * 2 + 90 * 3 + 28 * 4;
    const std::string long_component(240, 'a');

    const auto amplified_source = [&long_component](const std::size_t index) {
        return "res://amp/" + std::to_string(index) + "/" + long_component + "/" + long_component + "/" +
               long_component + "/" + long_component + "/leaf.png";
    };
    const auto conventional_source = [&long_component](const std::size_t index) {
        return "res://conv/" + std::to_string(index) + "/" + long_component + "/" + long_component + "/" +
               long_component + "/" + long_component + "/leaf.png";
    };

    const std::size_t virtual_length = amplified_source(0).size() - 6;
    const std::size_t compiled_length = std::string(".godot/imported/amp0.ctex").size();
    const std::size_t owned_per_fragment =
        mappings_per_fragment * (virtual_length + compiled_length + 3 * sizeof(std::string)) + variant_bytes;
    const std::size_t fragments = (alias_allowance - residual_target) / owned_per_fragment;

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    for (std::size_t index = 0; index < fragments; ++index) {
        options.entries.push_back(entry("res://amp/" + std::to_string(index) + ".import",
                                        as_u8(amplified_metadata(amplified_source(index), mappings_per_fragment))));
    }
    for (std::size_t group = 0; group < conventional_groups; ++group) {
        const std::string source = conventional_source(group);
        const std::string hash = md5_hex(source);
        const std::string metadata = remap_metadata(source, "res://.godot/imported/leaf.png-" + hash + ".v0.ctex");
        options.entries.push_back(entry(source + ".import", as_u8(metadata)));
        for (std::size_t variant = 0; variant < variants_per_group; ++variant) {
            options.entries.push_back(
                entry("res://.godot/imported/leaf.png-" + hash + ".v" + std::to_string(variant) + ".ctex",
                      as_u8(compiled_red_texture())));
        }
    }

    const temporary_archive archive("godot-conventional-budget", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const std::string first_source = conventional_source(0);
    const std::string first_stem = first_source.substr(6, first_source.size() - 10);
    CHECK(find_file(files, first_stem + ".v0.png") != nullptr);

    const std::string last_source = conventional_source(conventional_groups - 1);
    const std::string last_stem = last_source.substr(6, last_source.size() - 10);
    const test::file *manifest = find_file(files, "name-map.tsv");
    REQUIRE(manifest != nullptr);
    const std::string document = extract_bytes(*manifest, destination);
    std::vector<std::pair<std::string, std::string>> recovered;
    for (std::size_t start = 0; start < document.size();) {
        const std::size_t end = document.find('\n', start);
        REQUIRE(end != std::string::npos);
        const std::vector<std::string> fields = tab_fields(std::string_view(document).substr(start, end - start));
        REQUIRE(fields.size() == 3);
        recovered.emplace_back(unescape_field(fields[1]), unescape_field(fields[2]));
        start = end + 1;
    }
    const auto recovered_path = [&recovered](const std::string_view physical) {
        const auto found =
            std::ranges::find_if(recovered, [physical](const auto &row) { return row.first == physical; });
        return found == recovered.end() ? std::string{} : found->second;
    };

    for (std::size_t group = 0; group < conventional_groups; ++group) {
        const std::string hash = md5_hex(conventional_source(group));
        for (std::size_t variant = 0; variant < variants_per_group; ++variant) {
            CAPTURE(group, variant);
            const std::string stored = ".godot/imported/leaf.png-" + hash + ".v" + std::to_string(variant) + ".ctex";
            const std::string final_path = recovered_path(stored);
            REQUIRE(!final_path.empty());
            const test::file *view = find_file(files, final_path);
            REQUIRE(view != nullptr);
            CHECK(decode_png_rgba(extract_bytes(*view, destination)) ==
                  std::vector<std::byte>{std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255}});
        }
    }

    for (std::size_t variant = 0; variant < variants_per_group; ++variant) {
        CAPTURE(variant);
        CHECK(find_file(files, last_stem + ".v" + std::to_string(variant) + ".png") == nullptr);
    }
}

TEST_CASE("godot integration: conflicting conventional directories fall back to metadata aliases",
          "[integration][godot][filename-extension][conventional-ambiguous]")
{
    const std::string source_path = "res://art/dual.png";
    const std::string hash = md5_hex(source_path);
    const std::string body_target = "res://.godot/imported/from-body.ctex";
    const std::string metadata = remap_metadata(source_path, body_target);

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://art/dual.png.import", as_u8(metadata)));
    options.entries.push_back(entry("res://.godot/imported/dual.png-" + hash + ".ctex", as_u8(compiled_texture())));
    options.entries.push_back(entry("res://.import/dual.png-" + hash + ".ctex", as_u8(compiled_texture())));
    options.entries.push_back(entry(body_target, as_u8(compiled_texture())));

    const temporary_archive archive("godot-conventional-ambiguous", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());

    const test::file *alias = find_file(files, "art/dual.png");
    REQUIRE(alias != nullptr);
    CHECK(extract_bytes(*alias, archive.path().wstring() + L".out") == as_string(png_bytes(32)));
}

TEST_CASE("godot integration: extensionless alias roots stay raw without probing",
          "[integration][godot][filename-extension][alias-no-extension]")
{
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(
        entry("res://art/noext.remap", as_u8(remap_metadata("res://art/noext", "res://.godot/imported/noext.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/noext.ctex", as_u8(compiled_texture())));
    options.entries.push_back(entry("res://art.dir/name.remap",
                                    as_u8(remap_metadata("res://art.dir/name", "res://.godot/imported/name.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/name.ctex", as_u8(compiled_texture())));

    const temporary_archive archive("godot-extensionless-aliases", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *no_extension = find_file(files, "art/noext.ctex");
    REQUIRE(no_extension != nullptr);
    CHECK(extract_bytes(*no_extension, destination) == as_string(compiled_texture()));
    const test::file *dot_before_slash = find_file(files, "art.dir/name.ctex");
    REQUIRE(dot_before_slash != nullptr);
    CHECK(extract_bytes(*dot_before_slash, destination) == as_string(compiled_texture()));
}

TEST_CASE("godot integration: a root source filename with an extension maps its compiled alias",
          "[integration][godot][filename-extension][root-alias]")
{
    const std::string compiled = "res://.godot/imported/custom.ctex";
    const std::string metadata = remap_metadata("res://picture.png", compiled);
    const godot_bytes texture = compiled_texture();

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://picture.png.import", as_u8(metadata)));
    options.entries.push_back(entry(compiled, texture));

    const temporary_archive archive("godot-root-alias", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *alias = find_file(files, "picture.png");
    REQUIRE(alias != nullptr);
    CHECK(alias->compressed_size == static_cast<int64_t>(texture.size()));
    CHECK(extract_bytes(*alias, destination) == as_string(png_bytes(32)));
    CHECK(find_file(files, ".godot/imported/custom.ctex") == nullptr);

    const test::file *metadata_file = find_file(files, "picture.png.import");
    REQUIRE(metadata_file != nullptr);
    CHECK(extract_bytes(*metadata_file, destination) == metadata);
}

TEST_CASE("godot integration: a '.remap' fragment maps an alias exactly like a '.import'", "[integration][godot]")
{
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    const std::string remap = remap_metadata("res://art/b.png", "res://.godot/imported/b.ctex");
    options.entries.push_back(entry("res://art/b.png.remap", as_u8(remap)));
    options.entries.push_back(entry("res://.godot/imported/b.ctex", as_u8(compiled_texture())));

    const temporary_archive archive("godot-remap", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The ".remap" metadata remains directly reachable beside the restored converted resource.
    CHECK(find_file(files, "art/b.png.remap") != nullptr);
    const test::file *metadata = find_file(files, "art/b.png.remap");
    REQUIRE(metadata != nullptr);
    CHECK(extract_bytes(*metadata, destination) == remap);

    const test::file *alias = find_file(files, "art/b.png");
    REQUIRE(alias != nullptr);
    CHECK(extract_bytes(*alias, destination) == as_string(png_bytes(32)));
}

TEST_CASE("godot integration: an empty metadata fragment is skipped without failing the listing",
          "[integration][godot]")
{
    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry("res://art/e.png.import", std::vector<std::uint8_t>{}));
    options.entries.push_back(entry("res://keep.txt", as_u8("keep")));

    const temporary_archive archive("godot-empty-metadata", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // An empty fragment declares no alias, is never read as a body, and is preserved byte for byte
    // (an empty file) in the main tree while the unrelated source stays reachable.
    CHECK(find_file(files, "art/e.png") == nullptr);
    const test::file *metadata = find_file(files, "art/e.png.import");
    REQUIRE(metadata != nullptr);
    CHECK(metadata->compressed_size == 0);
    CHECK(metadata->uncompressed_size == 0);
    CHECK(extract_bytes(*metadata, destination).empty());

    const test::file *kept = find_file(files, "keep.txt");
    REQUIRE(kept != nullptr);
    CHECK(extract_bytes(*kept, destination) == "keep");
}

TEST_CASE("godot integration: a metadata fragment above the per-body cap keeps its raw original",
          "[integration][godot]")
{
    constexpr std::size_t oversized_body = 4 * 1024 * 1024 + 1;
    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry("res://junk/huge.import", std::vector<std::uint8_t>(oversized_body, 'x')));
    options.entries.push_back(entry("res://keep.txt", as_u8("keep")));

    const temporary_archive archive("godot-oversized-metadata", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // A metadata body one byte past the 4 MiB per-fragment cap is never parsed as metadata, yet the
    // physical source is listed raw and extracts byte for byte at its honest stored size.
    const test::file *original = find_file(files, "junk/huge.import");
    REQUIRE(original != nullptr);
    CHECK(original->compressed_size == static_cast<int64_t>(oversized_body));
    CHECK(extract_bytes(*original, destination) == std::string(oversized_body, 'x'));

    const test::file *kept = find_file(files, "keep.txt");
    REQUIRE(kept != nullptr);
    CHECK(extract_bytes(*kept, destination) == "keep");
}

TEST_CASE("godot integration: a fragment whose transient peak no longer fits the residual stops the pass",
          "[integration][godot]")
{
    // The transient-peak ceiling reserves room for the live copies of one fragment's parse. Keep
    // every amplified path within the parser's 4092-byte cap, retain just under the 32 MiB alias
    // ceiling, then present a 4 MiB body whose 8-copy peak exceeds the residual 64 MiB allowance.
    // The late valid remap must not be parsed, while every physical source remains extractable.
    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};

    constexpr std::size_t virtual_length = 500;
    constexpr std::size_t compiled_length = 1000;
    constexpr std::size_t mappings_per_fragment = 128;
    // The driver charges each mapping's two path lengths, variant length and a three-string slot.
    // The variants v0..v127 occupy 402 bytes together. Compute the fragment count from the actual
    // build's std::string size, leaving 256 KiB beneath the 32 MiB retained ceiling. A full 4 MiB
    // body's peak is over 33 MiB, so it cannot fit the less-than-33 MiB transient residual.
    constexpr std::size_t variant_bytes = 10 * 2 + 90 * 3 + 28 * 4;
    constexpr std::size_t owned_per_fragment =
        mappings_per_fragment * (virtual_length + compiled_length + 3 * sizeof(std::string)) + variant_bytes;
    constexpr std::size_t fragments = (32 * 1024 * 1024 - 256 * 1024) / owned_per_fragment;
    const std::string component(249, 'c');
    const std::string compiled = "c/" + component + "/" + component + "/" + component + "/" +
                                 std::string(248, 'c'); // 1000 bytes; each component fits on Windows
    REQUIRE(compiled.size() == compiled_length);

    const auto fragment_text = [&compiled](const std::size_t index) {
        const std::string stem = "v" + std::to_string(index) + "_";
        const std::string source =
            stem + std::string(240 - stem.size(), 'a') + "/" + std::string(240, 'a') + "/" + std::string(18, 'a');
        REQUIRE(source.size() == virtual_length);
        std::string text = "[remap]\n";
        for (std::size_t variant = 0; variant < mappings_per_fragment; ++variant) {
            text += "path.v" + std::to_string(variant) + "=\"" + compiled + "\"\n";
        }
        text += "[deps]\nsource_file=\"" + source + "\"\n";
        return text;
    };

    options.entries.push_back(entry("res://art/early.png.import",
                                    as_u8(remap_metadata("res://art/early.png", "res://.godot/imported/early.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/early.ctex", as_u8(compiled_texture())));
    for (std::size_t index = 0; index < fragments; ++index) {
        options.entries.push_back(entry("res://amp/" + std::to_string(index) + ".import", as_u8(fragment_text(index))));
    }
    options.entries.push_back(
        entry("res://amp/trip.import", std::vector<std::uint8_t>(std::size_t{4} * 1024 * 1024, 'z')));
    options.entries.push_back(entry("res://art/late.png.remap",
                                    as_u8(remap_metadata("res://art/late.png", "res://.godot/imported/late.ctex"))));
    options.entries.push_back(entry("res://.godot/imported/late.ctex", as_u8(compiled_texture())));
    options.entries.push_back(entry("res://late/keep.bin", {0x51, 0x52, 0x53}));

    const temporary_archive archive("godot-transient-peak", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *early = find_file(files, "art/early.png");
    REQUIRE(early != nullptr);
    CHECK(extract_bytes(*early, destination) == as_string(png_bytes(32)));
    CHECK(find_file(files, "art/late.png") == nullptr);

    // The metadata pass stops at trip. Every source still has a visible view: only the early texture
    // has an alias, while the late generic compiled name remains byte-exact under its stored name.
    for (const godot_entry_options &stored : options.entries) {
        REQUIRE(stored.path.starts_with("res://"));
        const bool early_texture = stored.path == "res://.godot/imported/early.ctex";
        const std::string path = early_texture ? "art/early.png" : stored.path.substr(6);
        const test::file *view = find_file(files, path);
        REQUIRE(view != nullptr);
        CHECK(view->compressed_size == static_cast<int64_t>(stored.payload.size()));
        CHECK(extract_bytes(*view, destination) ==
              (early_texture ? as_string(png_bytes(32)) : as_string(stored.payload)));
    }
}

TEST_CASE("godot integration: converted listing reports packed size while extraction preserves the PNG",
          "[integration][godot][converted-size]")
{
    const std::filesystem::path fixture =
        std::filesystem::path(__FILE__).parent_path().parent_path() / "data" / "godot" / "converted-size.pck";
    const temporary_archive archive("godot-converted-size", as_u8(read_back(fixture)));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const test::file *converted = find_file(files, "art/pixel.png");
    const test::file *original = find_file(files, "art/pixel.ctex");
    REQUIRE(converted != nullptr);
    CHECK(original == nullptr);
    CHECK(converted->compressed_size == 126);
    const std::string output = extract_bytes(*converted, archive.path().wstring() + L".out");
    REQUIRE(output.size() == 70);
    CHECK(converted->uncompressed_size == converted->compressed_size);
}

TEST_CASE("godot integration: an expensive resource cannot starve a later encoded texture",
          "[integration][godot][probe-budget-fixture]")
{
    const auto fixture =
        std::filesystem::path(__FILE__).parent_path().parent_path() / "data" / "godot" / "probe-budget-starvation.pck";
    const std::string packed = read_back(fixture);
    const temporary_archive archive("godot-probe-budget", as_u8(packed));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // The malformed RSCC declares 64 MiB + 1 logical bytes. It remains raw while a later
    // independent encoded texture is still recognised and extracted.
    const test::file *trigger = find_file(files, "budget/large.res");
    REQUIRE(trigger != nullptr);
    CHECK(trigger->uncompressed_size == 40);
    CHECK(extract_bytes(*trigger, destination) == packed.substr(320, 40));

    const test::file *converted = find_file(files, "art/pixel.png");
    REQUIRE(converted != nullptr);
    CHECK(find_file(files, "art/pixel.ctex") == nullptr);
    CHECK(converted->compressed_size == 126);
    CHECK(converted->uncompressed_size == converted->compressed_size);
    // This compares the full valid red-pixel PNG with its stored base-level window.
    CHECK(extract_bytes(*converted, destination) == packed.substr(320 + 40 + 56, 70));

    const test::file *metadata = find_file(files, "art/pixel.png.import");
    REQUIRE(metadata != nullptr);
    CHECK(extract_bytes(*metadata, destination) == packed.substr(320 + 40 + 126, 97));
}

TEST_CASE("godot integration: synthetic QOA resources restore omitted engine defaults",
          "[integration][godot][audio-defaults-fixture]")
{
    const auto fixture =
        std::filesystem::path(__FILE__).parent_path().parent_path() / "data" / "godot" / "audio-defaults.pck";
    const temporary_archive archive("godot-audio-defaults", as_u8(read_back(fixture)));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";
    struct expected_wave final
    {
        std::string_view stem;
        std::uint32_t channels;
        std::uint32_t rate;
        int64_t packed_size;
    };
    const expected_wave cases[] = {
        {"mono-default", 1, 44100, 276},
        {"mono-low", 1, 22050, 288},
        {"stereo-default", 2, 44100, 312},
    };
    for (const expected_wave &expected : cases) {
        CAPTURE(expected.stem);
        const std::string path = ".godot/imported/" + std::string(expected.stem);
        const test::file *converted = find_file(files, path + ".wav");
        REQUIRE(converted != nullptr);
        const std::string source_path = "res://audio/" + std::string(expected.stem) + ".wav";
        CHECK(find_file(files, path + ".wav-" + md5_hex(source_path) + ".sample") == nullptr);
        CHECK(converted->compressed_size == expected.packed_size);
        const std::string output = extract_bytes(*converted, destination);
        REQUIRE(output.size() == 44 + static_cast<std::size_t>(expected.channels) * 2);
        CHECK(converted->uncompressed_size == converted->compressed_size);
        CHECK(output.substr(0, 4) == "RIFF");
        CHECK(output.substr(8, 4) == "WAVE");
        const auto byte = [&output](const std::size_t at) {
            return static_cast<std::uint32_t>(static_cast<unsigned char>(output.at(at)));
        };
        CHECK((byte(22) | byte(23) << 8U) == expected.channels);
        CHECK((byte(24) | byte(25) << 8U | byte(26) << 16U | byte(27) << 24U) == expected.rate);
        CHECK((byte(34) | byte(35) << 8U) == 16);
        for (std::uint32_t channel = 0; channel < expected.channels; ++channel) {
            const std::size_t offset = 44 + static_cast<std::size_t>(channel) * 2;
            CHECK(byte(offset) == 1);
            CHECK(byte(offset + 1) == 0);
        }
    }
}

TEST_CASE("godot integration: a hashed imported filename supplies the decoded name without reading its body",
          "[integration][godot][filename-extension][lazy-payload]")
{
    const std::string hash = "00112233445566778899aabbccddeeff";
    const std::string source_path = "res://.godot/imported/picture.png-" + hash + ".ctex";
    const godot_bytes texture = compiled_texture();

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    // Push the target body beyond the locator's 128 KiB analysis prefix so the lock below
    // isolates payload reads from pack recognition.
    options.entries.push_back(entry("res://padding/keep.bin", godot_bytes(std::size_t{160} * 1024, 0x5A)));
    options.entries.push_back(entry(source_path, texture));

    const godot_bytes packed = build_godot_pck(options);
    const std::uint64_t body_offset = payload_offset(packed, texture);
    const temporary_archive archive("godot-filename-lazy", packed);
    const test::observer plugin;

    const auto files = [&] {
        const locked_file_range lock(archive.path(), body_offset, texture.size());
        return plugin.list_files(archive.path());
    }();

    CHECK(find_file(files, "padding/keep.bin") != nullptr);
    const test::file *converted = find_file(files, ".godot/imported/picture.png");
    REQUIRE(converted != nullptr);
    CHECK(find_file(files, ".godot/imported/picture.png-" + hash + ".ctex") == nullptr);
    CHECK(converted->compressed_size == static_cast<int64_t>(texture.size()));
    CHECK(extract_bytes(*converted, archive.path().wstring() + L".out") == as_string(png_bytes(32)));
}

TEST_CASE("godot integration: a conventional import and its media body stay unread during listing",
          "[integration][godot][filename-extension][conventional-fast-path]")
{
    const std::string source_path = "res://art/picture.png";
    const std::string hash = md5_hex(source_path);
    const std::string compiled_path = "res://.godot/imported/picture.png-" + hash + ".ctex";
    const std::string metadata = remap_metadata(source_path, compiled_path);
    const godot_bytes texture = compiled_texture();

    godot_pck_options options;
    options.version = 4;
    options.engine_version = {4, 0, 0};
    options.entries.push_back(entry("res://padding/keep.bin", godot_bytes(std::size_t{160} * 1024, 0x5A)));
    options.entries.push_back(entry("res://art/picture.png.import", as_u8(metadata)));
    options.entries.push_back(entry(compiled_path, texture));

    const godot_bytes packed = build_godot_pck(options);
    const std::uint64_t metadata_offset = payload_offset(packed, as_u8(metadata));
    const std::uint64_t media_offset = payload_offset(packed, texture);
    REQUIRE(metadata_offset > std::uint64_t{128} * 1024);
    REQUIRE(media_offset > std::uint64_t{128} * 1024);

    const temporary_archive archive("godot-conventional-fast", packed);
    const test::observer plugin;
    const auto files = [&] {
        const locked_file_range metadata_lock(archive.path(), metadata_offset, metadata.size());
        const locked_file_range media_lock(archive.path(), media_offset, texture.size());
        return plugin.list_files(archive.path());
    }();

    // The exact conventional name/hash pair restores the original path without reading either
    // body. The imported metadata and the compiled body remain byte-exact after the locks release.
    const test::file *converted = find_file(files, "art/picture.png");
    REQUIRE(converted != nullptr);
    CHECK(find_file(files, ".godot/imported/picture.png-" + hash + ".ctex") == nullptr);
    CHECK(converted->compressed_size == static_cast<int64_t>(texture.size()));
    const auto destination = archive.path().wstring() + L".out";
    CHECK(extract_bytes(*converted, destination) == as_string(png_bytes(32)));
    const test::file *stored_metadata = find_file(files, "art/picture.png.import");
    REQUIRE(stored_metadata != nullptr);
    CHECK(extract_bytes(*stored_metadata, destination) == metadata);
}

TEST_CASE("godot integration: filename candidates reject malformed suffixes and defer corrupt bodies",
          "[integration][godot][filename-extension][corrupt-deferred]")
{
    const std::string hash = "00112233445566778899aabbccddeeff";
    const godot_bytes texture = compiled_texture();
    const godot_bytes corrupt = {'G', 'S', 'T', '2', 1, 0, 0, 0, 0, 0, 0, 0};

    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry("res://.godot/imported/Upper.PNG-" + hash + ".ctex", texture));
    options.entries.push_back(entry("res://.godot/imported/bad.PNG-nothex.ctex", texture));
    options.entries.push_back(entry("res://.godot/imported/broken.png-" + hash + ".ctex", corrupt));
    options.entries.push_back(entry("res://.godot/imported/picture.svg-" + hash + ".ctex", texture));
    options.entries.push_back(entry("res://.godot/imported/unknown.res", as_u8(plain_resource())));

    const temporary_archive archive("godot-filename-shapes", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    // Case is matched without inventing a second spelling of the same source suffix.
    const test::file *upper = find_file(files, ".godot/imported/Upper.PNG");
    if (upper == nullptr) {
        upper = find_file(files, ".godot/imported/Upper.png");
    }
    REQUIRE(upper != nullptr);
    CHECK(extract_bytes(*upper, destination) == as_string(png_bytes(32)));

    // A source suffix is recognized only immediately before a complete 32-digit hash. A malformed
    // name keeps the complete stored name and is never rescued by probing its healthy body.
    const test::file *malformed = find_file(files, ".godot/imported/bad.PNG-nothex.ctex");
    REQUIRE(malformed != nullptr);
    CHECK(extract_bytes(*malformed, destination) == as_string(texture));

    // A syntactically valid source name is enough to expose the converted path. Its corrupt body
    // is deferred to extraction, so it cannot make the whole listing fail.
    const test::file *broken = find_file(files, ".godot/imported/broken.png");
    REQUIRE(broken != nullptr);
    CHECK(find_file(files, ".godot/imported/broken.png-" + hash + ".ctex") == nullptr);

    // SVG is not a truthful output encoding for this stored texture.
    CHECK(find_file(files, ".godot/imported/picture.svg") == nullptr);
    CHECK(find_file(files, ".godot/imported/picture.svg.png") == nullptr);

    // A compiled .res without an embedded source suffix stays raw and byte-exact.
    const test::file *unknown = find_file(files, ".godot/imported/unknown.res");
    REQUIRE(unknown != nullptr);
    CHECK(extract_bytes(*unknown, destination) == as_string(plain_resource()));
}

TEST_CASE("godot integration: an import alias drives the output name from its original suffix",
          "[integration][godot][filename-extension][import-alias]")
{
    const std::string hash = "00112233445566778899aabbccddeeff";
    const std::string compiled = "res://.godot/imported/opaque-" + hash + ".ctex";
    const std::string metadata = remap_metadata("res://art/Renamed.PNG", compiled);

    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry("res://art/Renamed.PNG.import", as_u8(metadata)));
    options.entries.push_back(entry(compiled, compiled_texture()));

    const temporary_archive archive("godot-filename-alias", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());
    const auto destination = archive.path().wstring() + L".out";

    const test::file *alias = find_file(files, "art/Renamed.PNG");
    REQUIRE(alias != nullptr);
    CHECK(extract_bytes(*alias, destination) == as_string(png_bytes(32)));
    const test::file *metadata_file = find_file(files, "art/Renamed.PNG.import");
    REQUIRE(metadata_file != nullptr);
    CHECK(extract_bytes(*metadata_file, destination) == metadata);
}

TEST_CASE("godot integration: a PNG source name does not expose a WebP body as a WebP conversion",
          "[integration][godot][filename-extension][encoding-mismatch]")
{
    const std::string hash = "00112233445566778899aabbccddeeff";
    const std::string source_path = "res://.godot/imported/mismatch.png-" + hash + ".ctex";

    godot_pck_options options;
    options.version = 4;
    options.entries.push_back(entry(source_path, compiled_webp_texture()));

    const temporary_archive archive("godot-filename-mismatch", build_godot_pck(options));
    const test::observer plugin;
    const auto files = plugin.list_files(archive.path());

    // The filename chooses the conversion candidate and output name. Stored WebP cannot silently
    // replace that selection; the extraction-side codec must either produce PNG or reject the
    // mismatch instead of writing RIFF bytes under the selected .png name.
    CHECK(find_file(files, ".godot/imported/mismatch.png") != nullptr);
    CHECK(find_file(files, ".godot/imported/mismatch.webp") == nullptr);
}
