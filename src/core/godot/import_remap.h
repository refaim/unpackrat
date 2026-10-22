#ifndef UNPACKRAT_CORE_GODOT_IMPORT_REMAP_H
#define UNPACKRAT_CORE_GODOT_IMPORT_REMAP_H

#include <cstddef>
#include <optional>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace unpackrat::godot
{
    // One virtual resource path bound to one compiled resource inside the archive.
    // ``variant`` is empty for the base ``path`` key and holds the feature suffix
    // (``s3tc``, ``etc2``, ...) for ``path.<feature>`` keys.
    struct import_mapping
    {
        std::string virtual_path;
        std::string compiled_path;
        std::string variant;
    };

    struct metadata_limits
    {
        std::size_t max_input_bytes = std::size_t{4} * 1024 * 1024;
        std::size_t max_mappings = 128;
        std::size_t max_nesting = 16;
    };

    class metadata_error final : public std::runtime_error
    {
      public:
        metadata_error() : std::runtime_error("godot import/remap metadata is malformed")
        {
        }
    };

    // Parse a bounded Godot ``.import``/``.remap`` ConfigFile fragment. The
    // metadata never touches the filesystem or evaluates any script/resource.
    [[nodiscard]] std::vector<import_mapping> read_import_remap(std::span<const std::byte> bytes,
                                                                std::string_view metadata_path,
                                                                const metadata_limits &limits = {});

    // One conventional compiled import filename recognised from its own name only:
    // ``<imported-dir>/<basename>.<source-extension>-<32 hex>[.<feature>].<compiled-extension>``.
    // ``key`` and ``source_path`` are views into the supplied path.
    struct imported_name final
    {
        std::string_view key;         // path through the 32-digit hash, feature excluded
        std::string_view source_path; // original path through the source extension
        std::string output_extension; // truthful output extension for this source suffix
        std::string_view variant;     // conventional feature token, empty when absent
    };

    // Classifies one stored imported filename. Only the exact ``.godot/imported`` and ``.import``
    // directories, a supported original suffix immediately before a complete lowercase/uppercase
    // 32-digit hash, and at most one alphanumeric feature token are accepted. Anything else keeps
    // its raw name.
    [[nodiscard]] std::optional<imported_name> classify_imported_name(std::string_view path);

    // The deterministic output extension for a supported original source suffix. ``.png``,
    // ``.webp``, ``.wav``, ``.ogg``, ``.mp3`` and supported font suffixes preserve their spelling
    // so a case-preserving path remains stable; ``.jpg``/``.jpeg``/``.svg`` select ``.png``.
    [[nodiscard]] std::optional<std::string> output_extension_for_source(std::string_view source_extension);

    // The original source path carried by an ``.import`` filename, or no value when the name is not
    // a supported import-metadata name.
    [[nodiscard]] std::optional<std::string> conventional_import_source_path(std::string_view metadata_path);

    // ``<basename>-<md5(canonical res:// source path)>`` for the official ResourceFormatImporter
    // naming rule. The source path is archive-relative (no ``res://`` prefix).
    [[nodiscard]] std::string conventional_import_basename(std::string_view source_path);
} // namespace unpackrat::godot
#endif
