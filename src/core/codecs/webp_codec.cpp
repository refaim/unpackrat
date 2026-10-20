#include "webp_codec.h"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#include <string>

// libwebp is compiled into this module as a static port; the macro stays private to this
// translation unit so no external declaration or allocated buffer escapes the adapter.
#ifndef WEBP_STATIC
#define WEBP_STATIC
#endif
#include <webp/decode.h>
#include <webp/encode.h>

namespace unpackrat::codecs
{
    namespace
    {
        constexpr std::uint64_t rgba_channels = 4;
        constexpr std::uint32_t max_rgba_width =
            static_cast<std::uint32_t>((std::numeric_limits<int>::max)() / rgba_channels);

        struct rgba_geometry final
        {
            std::uint64_t bytes = 0;
            std::size_t stride = 0;
        };

        // Validates every dimension the library call needs before any allocation or cast. The RGBA
        // row stride and both dimensions must fit the library's int parameters, the byte count must
        // fit the platform's size_t, and the caller's pixel budget is applied to width * height.
        rgba_geometry validate_geometry(const std::uint32_t width, const std::uint32_t height,
                                        const image_limits &limits)
        {
            if (width == 0 || height == 0) {
                throw image_error("WebP dimensions must be non-zero");
            }
            const std::uint64_t pixels = static_cast<std::uint64_t>(width) * height;
            if (pixels > limits.max_pixels) {
                throw image_error("WebP image exceeds the configured pixel budget");
            }
            if (pixels > (std::numeric_limits<std::uint64_t>::max)() / rgba_channels) {
                throw image_error("WebP dimensions overflow the byte count");
            }
            if (width > max_rgba_width) {
                throw image_error("WebP row stride does not fit an int");
            }
            if (height > static_cast<std::uint32_t>((std::numeric_limits<int>::max)())) {
                throw image_error("WebP height does not fit an int");
            }
            const std::uint64_t bytes = pixels * rgba_channels;
#if SIZE_MAX < UINT64_MAX
            if (bytes > static_cast<std::uint64_t>((std::numeric_limits<std::size_t>::max)())) {
                throw image_error("WebP byte count is not addressable");
            }
#endif
            return rgba_geometry{bytes, static_cast<std::size_t>(width) * static_cast<std::size_t>(rgba_channels)};
        }

        // The picture owns every buffer WebPPictureImportRGBA/WebPEncode allocate; the writer
        // owns the encoded stream. Both release functions accept an initialized, empty value.
        struct webp_picture_guard final
        {
            webp_picture_guard()
            {
                static_cast<void>(WebPPictureInit(&value));
            }
            ~webp_picture_guard()
            {
                WebPPictureFree(&value);
            }
            webp_picture_guard(const webp_picture_guard &) = delete;
            webp_picture_guard &operator=(const webp_picture_guard &) = delete;

            WebPPicture value{};
        };

        struct webp_writer_guard final
        {
            webp_writer_guard()
            {
                WebPMemoryWriterInit(&value);
            }
            ~webp_writer_guard()
            {
                WebPMemoryWriterClear(&value);
            }
            webp_writer_guard(const webp_writer_guard &) = delete;
            webp_writer_guard &operator=(const webp_writer_guard &) = delete;

            WebPMemoryWriter value{};
        };

        [[noreturn]] void decoding_error()
        {
            throw image_error("WebP decoding failed");
        }
    } // namespace

    rgba_image decode_webp(const std::span<const std::byte> encoded, const image_limits &limits)
    {
        if (encoded.size() > limits.max_input_bytes) {
            throw image_error("WebP input exceeds the configured byte budget");
        }
        int width = 0;
        int height = 0;
        if (WebPGetInfo(reinterpret_cast<const std::uint8_t *>(encoded.data()), encoded.size(), &width, &height) == 0) {
            decoding_error();
        }
        rgba_image image;
        image.width = static_cast<std::uint32_t>(width);
        image.height = static_cast<std::uint32_t>(height);
        const rgba_geometry geometry = validate_geometry(image.width, image.height, limits);
        if (geometry.bytes > limits.max_output_bytes) {
            throw image_error("WebP decoded image exceeds the configured byte budget");
        }
        image.pixels.resize(static_cast<std::size_t>(geometry.bytes));
        const std::uint8_t *decoded =
            WebPDecodeRGBAInto(reinterpret_cast<const std::uint8_t *>(encoded.data()), encoded.size(),
                               reinterpret_cast<std::uint8_t *>(image.pixels.data()), image.pixels.size(),
                               static_cast<int>(geometry.stride));
        if (decoded == nullptr) {
            decoding_error();
        }
        return image;
    }

    void encode_webp_lossless(const rgba_image &image, const std::function<void(std::span<const std::byte>)> &sink,
                              const image_limits &limits)
    {
        const rgba_geometry geometry = validate_geometry(image.width, image.height, limits);
        if (geometry.bytes > limits.max_input_bytes) {
            throw image_error("WebP raw input exceeds the configured byte budget");
        }
        if (geometry.bytes != image.pixels.size()) {
            throw image_error("WebP pixel buffer does not match the declared dimensions");
        }

        // The encoder headers and the static library are paired at build time, so the init entry
        // points cannot observe an ABI mismatch; their return value is not a runtime condition.
        WebPConfig config{};
        static_cast<void>(WebPConfigInit(&config));
        static_cast<void>(WebPConfigLosslessPreset(&config, 6));
        // Lossless exact=1 preserves RGB values under fully transparent pixels. The convenience
        // WebPEncodeLosslessRGBA leaves WebPConfig::exact at zero.
        config.exact = 1;

        webp_writer_guard writer;
        webp_picture_guard picture;
        picture.value.use_argb = 1;
        picture.value.width = static_cast<int>(image.width);
        picture.value.height = static_cast<int>(image.height);
        picture.value.writer = &WebPMemoryWrite;
        picture.value.custom_ptr = &writer.value;

        if (!WebPPictureImportRGBA(&picture.value, reinterpret_cast<const std::uint8_t *>(image.pixels.data()),
                                   static_cast<int>(geometry.stride))) {
            throw image_error("WebP encoding failed");
        }
        if (!WebPEncode(&config, &picture.value)) {
            throw image_error("WebP encoding failed");
        }
        if (writer.value.size > limits.max_output_bytes) {
            throw image_error("WebP output exceeds the configured byte budget");
        }
        sink(std::span<const std::byte>(reinterpret_cast<const std::byte *>(writer.value.mem), writer.value.size));
    }
} // namespace unpackrat::codecs
