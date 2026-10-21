#include "archive.h"
#include "modules/extractor.h"

#include <array>
#include <filesystem>
#include <fstream>
#include <span>
#include <stdexcept>

namespace archive
{
    archive::archive(std::unique_ptr<extractor::extractor> extractor) : extractor_(std::move(extractor))
    {
    }

    bool starts_with_bytes(std::span<const std::byte> data, std::span<const std::byte> signature) noexcept
    {
        if (signature.size() > data.size()) {
            return false;
        }
        return std::equal(signature.begin(), signature.end(), data.begin());
    }

    extractor::archive_info archive::open(const std::filesystem::path &path, const std::span<const std::byte> &data,
                                          const std::string_view password)
    {
        auto signature = extractor::extractor::get_signature();
        if (!data.empty() && !starts_with_bytes(data, signature)) {
            throw std::runtime_error("Signature mismatch");
        }

        auto source = std::make_unique<std::ifstream>(path, std::ios::binary);
        if (!source->is_open()) {
            throw std::runtime_error("Failed to open file");
        }
        source->exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ifstream::failbit) |
                                                               static_cast<unsigned>(std::ifstream::badbit)));

        // Old callbacks borrow extractor-owned state. Destroy them before releasing that
        // state or replacing the underlying stream when this object is opened again.
        opened_ = false;
        files_.clear();
        extractor_->reset_storage();
        stream_ = std::move(source);
        auto info = extractor_->get_archive_info(*stream_, data, path, password);
        opened_ = true;
        return info;
    }

    void archive::prepare_files()
    {
        if (!opened_) {
            throw read_error();
        }
        if (auto *catalogue = extractor_->indexed_files()) {
            try {
                catalogue->prepare();
            } catch (extractor::read_error &) {
                throw read_error();
            }
            return;
        }
        if (!files_.empty()) {
            return;
        }

        try {
            files_ = extractor_->list_files(*stream_);
        } catch (extractor::read_error &) {
            throw read_error();
        }

        for (const auto &file : files_) {
            std::ranges::replace(file->path, '/', '\\');
        }
    }

    const extractor::file &archive::get_file(const size_t index) const
    {
        if (index >= files_.size()) {
            throw std::out_of_range("Index out of range");
        }
        return *files_[index];
    }

    extractor::file_metadata archive::get_metadata(const size_t index) const
    {
        if (const auto *catalogue = extractor_->indexed_files()) {
            return catalogue->metadata(index);
        }
        const auto &file = get_file(index);
        const auto header_size = std::ssize(file.header);
        // Preserve the legacy C-string path behavior, including an embedded terminator.
        return {std::string_view(file.path.c_str()), header_size + file.uncompressed_body_size_in_bytes,
                header_size + file.compressed_body_size_in_bytes};
    }

    void archive::get_metadata_into(const size_t index, const std::span<char> buffer,
                                    extractor::file_metadata &out) const
    {
        if (const auto *catalogue = extractor_->indexed_files()) {
            // A lazy catalogue materialises the path into the caller buffer; a retained
            // provider keeps its own storage and is copied through metadata() unchanged.
            if (const auto column = catalogue->path_into(index, buffer)) {
                out.path = std::string_view(buffer.data(), column->written);
                out.size = column->size;
                out.packed_size = column->packed_size;
                return;
            }
            out = catalogue->metadata(index);
            return;
        }
        out = get_metadata(index);
    }

    std::optional<extractor::indexed_catalogue::column>
    archive::get_wide_metadata_into(const size_t index, const std::span<wchar_t> buffer) const
    {
        const auto *catalogue = extractor_->indexed_files();
        if (catalogue == nullptr) {
            return std::nullopt;
        }
        const auto column = catalogue->wide_path_into(index, buffer);
        if (column && column->written > buffer.size()) {
            // Reject a malformed producer before the caller can consume a short buffer.
            throw extractor::read_error();
        }
        return column;
    }

    void archive::extract_file(const size_t index, const std::filesystem::path &path,
                               const std::function<void(int64_t)> &report_progress) const
    {
        const auto *catalogue = extractor_->indexed_files();
        const extractor::file *legacy = nullptr;
        if (catalogue != nullptr) {
            if (index >= catalogue->count()) {
                throw std::out_of_range("Index out of range");
            }
        } else {
            legacy = &get_file(index);
        }
        std::ofstream output(path, std::ios::binary);
        if (!output.is_open()) {
            throw write_error();
        }
        output.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ofstream::failbit) |
                                                              static_cast<unsigned>(std::ofstream::badbit)));

        if (catalogue != nullptr || legacy->extract) {
            // A custom handler owns the whole destination body. On any failure the
            // partially written destination is discarded without masking the original
            // exception; a failed output open above never removes an existing file.
            const auto discard_partial = [&output, &path]() {
                output.exceptions(std::ios_base::goodbit);
                output.close();
                std::error_code error;
                std::filesystem::remove(path, error);
            };

            try {
                if (catalogue != nullptr) {
                    catalogue->extract(index, output, report_progress);
                } else {
                    legacy->extract(*stream_, output, report_progress);
                }
                output.close();
            } catch (const extractor::read_error &) {
                discard_partial();
                throw read_error();
            } catch (const std::ios_base::failure &) {
                discard_partial();
                throw write_error();
            } catch (...) {
                discard_partial();
                throw;
            }
            return;
        }

        const extractor::file &file = *legacy;
        constexpr int64_t buffer_size = 128LL * 1024;
        std::vector<char> buffer(buffer_size);

        try {
            if (!file.header.empty()) {
                output.write(file.header.data(), std::ssize(file.header));
            }

            try {
                stream_->seekg(file.offset);
            } catch (std::ios_base::failure &) {
                throw read_error();
            }

            uint32_t magic = file.magic;
            int64_t bytes_left = file.compressed_body_size_in_bytes;
            while (bytes_left > 0) {
                const auto chunk_size = static_cast<std::streamsize>(std::min(bytes_left, buffer_size));

                try {
                    stream_->read(buffer.data(), chunk_size);
                } catch (std::ios_base::failure &) {
                    throw read_error();
                }

                buffer.resize(static_cast<size_t>(chunk_size));
                magic = extractor_->decrypt(magic, buffer);
                output.write(buffer.data(), static_cast<std::streamsize>(buffer.size()));

                bytes_left -= chunk_size;
                report_progress(chunk_size);
            }

            output.close();
        } catch (std::ios_base::failure &) {
            throw write_error();
        }
    }
} // namespace archive
