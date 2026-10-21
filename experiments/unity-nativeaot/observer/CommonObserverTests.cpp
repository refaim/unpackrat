// Real common Observer ABI with the existing fake format, independent of Unity bindings.
#include "api.h"
#include "archive.h"
#include "tests/support/archive_lazy_fixtures.h"
#include <catch2/catch_test_macros.hpp>
#include <array>
#include <cstring>
#include <memory>
#include <stdexcept>
extern "C" int MODULE_EXPORT LoadSubModule(ModuleLoadParameters *);
extern "C" void MODULE_EXPORT UnloadSubModule();
namespace extractor {
version_info get_version_info() noexcept { return {{}, 1, 0}; }
void configure(const wchar_t *) noexcept {}
void reset_settings() noexcept {}
}
namespace {
module_cbs load_api() {
    ModuleLoadParameters params{}; params.StructSize = sizeof(params);
    REQUIRE(LoadSubModule(&params) == TRUE);
    return params.ApiFuncs;
}
class failing_index final : public extractor::storage_state, public extractor::indexed_catalogue {
public:
    bool prepare_logic = false;
    bool export_logic = false;
    bool export_allocation_failure = false;
    extractor::indexed_catalogue *indexed_files() noexcept override { return this; }
    void prepare() override {
        if (prepare_logic) throw std::logic_error("prepare");
    }
    size_t count() const noexcept override { return 1; }
    extractor::file_metadata metadata(size_t) const override { return {"entry", 0, 0}; }
    void extract(size_t, std::ostream &, const std::function<void(int64_t)> &) const override {
        if (export_allocation_failure) throw std::bad_alloc();
        if (export_logic) throw std::logic_error("export");
        throw std::runtime_error("export");
    }
};
}
TEST_CASE("common Observer: invalid ABI inputs initialize outputs and reject calls") {
    const auto api = load_api();
    ModuleLoadParameters invalid{};
    REQUIRE(LoadSubModule(nullptr) == FALSE);
    REQUIRE(LoadSubModule(&invalid) == FALSE);
    StorageGeneralInfo info{}; HANDLE handle = INVALID_HANDLE_VALUE;
    StorageOpenParams params{};
    REQUIRE(api.OpenStorage(params, nullptr, &info) == SOR_INVALID_FILE);
    REQUIRE(api.OpenStorage(params, &handle, nullptr) == SOR_INVALID_FILE);
    REQUIRE(handle == nullptr);
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_INVALID_FILE);
    params.StructSize = sizeof(params);
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_INVALID_FILE);
    params.FilePath = L"nonexistent-common-observer-file";
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_INVALID_FILE);
    REQUIRE(handle == nullptr);
    REQUIRE(api.PrepareFiles(nullptr) == FALSE);
    REQUIRE(api.GetItem(nullptr, 0, nullptr) == GET_ITEM_ERROR);
    archive::archive unopened(std::make_unique<extractor::extractor>());
    REQUIRE(api.PrepareFiles(&unopened) == FALSE);
    REQUIRE(api.GetItem(&unopened, -1, nullptr) == GET_ITEM_ERROR);
    REQUIRE(api.GetItem(&unopened, 0, nullptr) == GET_ITEM_ERROR);
    REQUIRE(api.ExtractItem(nullptr, {}) == SER_ERROR_SYSTEM);
    ExtractOperationParams extract{};
    extract.ItemIndex = -1;
    REQUIRE(api.ExtractItem(&unopened, extract) == SER_ERROR_SYSTEM);
    extract.ItemIndex = 0;
    REQUIRE(api.ExtractItem(&unopened, extract) == SER_ERROR_SYSTEM);
    extract.DestPath = L"unused-output";
    REQUIRE(api.ExtractItem(&unopened, extract) == SER_ERROR_SYSTEM);
    extract.Callbacks.FileProgress = [](void *, int64_t) { return TRUE; };
    REQUIRE(api.ExtractItem(&unopened, extract) == SER_ERROR_SYSTEM);
    api.CloseStorage(nullptr);
    UnloadSubModule();
}
TEST_CASE("common Observer: legacy format metadata and password failure remain intact") {
    const auto api = load_api();
    const std::array<uint8_t,4> bytes{'F','A','K','E'};
    const test::support::temporary_file source("common-abi", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);
    StorageOpenParams params{}; params.StructSize=sizeof(params); params.FilePath=source.path().c_str();
    params.Password="known"; params.Data=bytes.data(); params.DataSize=bytes.size();
    StorageGeneralInfo info{}; HANDLE handle=nullptr;
    REQUIRE(api.OpenStorage(params,&handle,&info)==SOR_SUCCESS);
    REQUIRE(std::wstring(info.Compression)==L"-");
    REQUIRE(state.archive_info_password=="known");
    REQUIRE(api.PrepareFiles(handle)==TRUE);
    api.CloseStorage(handle);
    state.storage_factory=[]()->std::unique_ptr<extractor::storage_state> {throw extractor::password_required();};
    REQUIRE(api.OpenStorage(params,&handle,&info)==SOR_PASSWORD_REQUIRED);
    REQUIRE(handle==nullptr);
    params.DataSize=0;
    state.storage_factory={};
    state.archive_info.compression=L""; state.archive_info.comment=L"";
    REQUIRE(api.OpenStorage(params,&handle,&info)==SOR_SUCCESS);
    REQUIRE(std::wstring(info.Comment)==L"-");
    api.CloseStorage(handle);
    UnloadSubModule();
}
TEST_CASE("common Observer: owner preparation and export errors preserve ABI results") {
    const auto api=load_api();
    const std::array<uint8_t,4> bytes{'F','A','K','E'};
    const test::support::temporary_file source("common-index",bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);
    bool prepare_logic=false, export_logic=false, allocation_failure=false, invalid_index=false;
    SECTION("logic preparation") {prepare_logic=true;}
    SECTION("runtime export") {}
    SECTION("logic export") {export_logic=true;}
    SECTION("allocation export") {allocation_failure=true;}
    SECTION("out of range owner export") {invalid_index=true;}
    state.storage_factory=[&] {auto index=std::make_unique<failing_index>();index->prepare_logic=prepare_logic;index->export_logic=export_logic;index->export_allocation_failure=allocation_failure;return index;};
    StorageOpenParams params{};params.StructSize=sizeof(params);params.FilePath=source.path().c_str();
    StorageGeneralInfo info{};HANDLE handle=nullptr;
    REQUIRE(api.OpenStorage(params,&handle,&info)==SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle)==(prepare_logic?FALSE:TRUE));
    if(!prepare_logic) {
        const auto path=test::support::unused_path("common-export");
        ExtractOperationParams extract{};extract.DestPath=path.c_str();extract.Callbacks.FileProgress=[](void *,int64_t){return TRUE;};
        extract.ItemIndex=invalid_index?1:0;
        REQUIRE(api.ExtractItem(handle,extract)==SER_ERROR_SYSTEM);
        REQUIRE_FALSE(std::filesystem::exists(path));
    }
    api.CloseStorage(handle);
    UnloadSubModule();
}
namespace
{
    constexpr wchar_t wide_test_path[] = L"Assets\\\u65e5\u672c\\0_\U0001F600.bin";
    constexpr int64_t wide_test_size = 41;
    constexpr int64_t wide_test_packed_size = 37;

    class fake_wide_index final : public extractor::storage_state, public extractor::indexed_catalogue
    {
      public:
        bool prepared = false;
        mutable int wide_calls = 0;
        mutable int extract_calls = 0;
        bool *destroyed = nullptr;

        ~fake_wide_index() override
        {
            if (destroyed != nullptr) {
                *destroyed = true;
            }
        }

        extractor::indexed_catalogue *indexed_files() noexcept override
        {
            return this;
        }

        void prepare() override
        {
            prepared = true;
        }

        size_t count() const noexcept override
        {
            return 1;
        }

        extractor::file_metadata metadata(size_t) const override
        {
            throw extractor::read_error();
        }

        std::optional<extractor::indexed_catalogue::column> path_into(size_t, std::span<char>) const override
        {
            // The old UTF-8 metadata route must not be used for this provider.
            throw extractor::read_error();
        }

        std::optional<extractor::indexed_catalogue::column> wide_path_into(
            size_t index, std::span<wchar_t> buffer) const override
        {
            ++wide_calls;
            if (index >= count()) {
                throw std::out_of_range("Index out of range");
            }
            const std::wstring_view path(wide_test_path);
            if (buffer.size() < path.size()) {
                throw extractor::read_error();
            }
            for (size_t i = 0; i < path.size(); ++i) {
                buffer[i] = path[i];
            }
            return extractor::indexed_catalogue::column{path.size(), wide_test_size,
                                                        wide_test_packed_size};
        }

        void extract(size_t, std::ostream &, const std::function<void(int64_t)> &) const override
        {
            ++extract_calls;
            throw std::runtime_error("unexpected export");
        }
    };

    class metadata_failure_index final : public extractor::storage_state, public extractor::indexed_catalogue
    {
      public:
        bool *destroyed = nullptr;

        ~metadata_failure_index() override
        {
            if (destroyed != nullptr) {
                *destroyed = true;
            }
        }

        extractor::indexed_catalogue *indexed_files() noexcept override
        {
            return this;
        }

        void prepare() override
        {
        }

        size_t count() const noexcept override
        {
            return 1;
        }

        extractor::file_metadata metadata(size_t) const override
        {
            throw extractor::read_error();
        }

        void extract(size_t, std::ostream &, const std::function<void(int64_t)> &) const override
        {
        }
    };
} // namespace

TEST_CASE("common Observer: GetItem takes the caller-owned wide path route", "[common][abi]")
{
    const auto api = load_api();
    const std::array<uint8_t, 4> bytes{'F', 'A', 'K', 'E'};
    const test::support::temporary_file source("common-wide", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);

    bool destroyed = false;
    auto owned = std::make_unique<fake_wide_index>();
    auto *provider = owned.get();
    provider->destroyed = &destroyed;
    state.storage_factory = [&owned] { return std::move(owned); };

    StorageOpenParams params{};
    params.StructSize = sizeof(params);
    params.FilePath = source.path().c_str();
    StorageGeneralInfo info{};
    HANDLE handle = nullptr;
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle) == TRUE);

    alignas(std::max_align_t) StorageItemInfo item{};
    const int result = api.GetItem(handle, 0, &item);
    const bool prepared = provider->prepared;
    const int wide_calls = provider->wide_calls;
    const int extract_calls = provider->extract_calls;
    api.CloseStorage(handle);

    REQUIRE(prepared);
    REQUIRE(result == GET_ITEM_OK);
    REQUIRE(std::wstring(item.Path) == std::wstring(wide_test_path));
    REQUIRE(item.Size == wide_test_size);
    REQUIRE(item.PackedSize == wide_test_packed_size);
    REQUIRE(wide_calls == 1);
    REQUIRE(extract_calls == 0);
    REQUIRE(destroyed);
    UnloadSubModule();
}

TEST_CASE("common Observer: GetItem metadata failure zeroes the ABI record and closes once",
          "[common][abi]")
{
    const auto api = load_api();
    const std::array<uint8_t, 4> bytes{'F', 'A', 'K', 'E'};
    const test::support::temporary_file source("common-wide-error", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);

    bool destroyed = false;
    auto owned = std::make_unique<metadata_failure_index>();
    owned->destroyed = &destroyed;
    state.storage_factory = [&owned] { return std::move(owned); };

    StorageOpenParams params{};
    params.StructSize = sizeof(params);
    params.FilePath = source.path().c_str();
    StorageGeneralInfo info{};
    HANDLE handle = nullptr;
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle) == TRUE);

    alignas(std::max_align_t) StorageItemInfo item;
    std::memset(&item, 0x7f, sizeof(item));
    const int result = api.GetItem(handle, 0, &item);
    api.CloseStorage(handle);

    REQUIRE(result == GET_ITEM_ERROR);
    const auto *record = reinterpret_cast<const unsigned char *>(&item);
    for (size_t i = 0; i < sizeof(item); ++i) {
        REQUIRE(record[i] == 0);
    }
    REQUIRE(destroyed);
    UnloadSubModule();
}
namespace
{
    class scripted_wide_index final : public extractor::storage_state, public extractor::indexed_catalogue
    {
      public:
        enum class mode
        {
            empty,
            oversize,
        };

        mode selected = mode::empty;
        mutable int wide_calls = 0;
        bool *destroyed = nullptr;

        ~scripted_wide_index() override
        {
            if (destroyed != nullptr) {
                *destroyed = true;
            }
        }

        extractor::indexed_catalogue *indexed_files() noexcept override
        {
            return this;
        }

        void prepare() override
        {
        }

        size_t count() const noexcept override
        {
            return 1;
        }

        extractor::file_metadata metadata(size_t) const override
        {
            throw extractor::read_error();
        }

        std::optional<extractor::indexed_catalogue::column> path_into(size_t,
                                                                      std::span<char>) const override
        {
            throw extractor::read_error();
        }

        std::optional<extractor::indexed_catalogue::column> wide_path_into(
            size_t index, std::span<wchar_t> buffer) const override
        {
            ++wide_calls;
            if (index >= count()) {
                throw std::out_of_range("Index out of range");
            }
            if (selected == mode::oversize) {
                return extractor::indexed_catalogue::column{buffer.size() + 1, 9, 8};
            }
            return extractor::indexed_catalogue::column{0, 9, 8};
        }

        void extract(size_t, std::ostream &, const std::function<void(int64_t)> &) const override
        {
        }
    };
} // namespace

TEST_CASE("common Observer: an empty wide path is a valid empty ABI path", "[common][abi]")
{
    const auto api = load_api();
    const std::array<uint8_t, 4> bytes{'F', 'A', 'K', 'E'};
    const test::support::temporary_file source("common-wide-empty", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);

    bool destroyed = false;
    auto owned = std::make_unique<scripted_wide_index>();
    owned->selected = scripted_wide_index::mode::empty;
    owned->destroyed = &destroyed;
    state.storage_factory = [&owned] { return std::move(owned); };

    StorageOpenParams params{};
    params.StructSize = sizeof(params);
    params.FilePath = source.path().c_str();
    StorageGeneralInfo info{};
    HANDLE handle = nullptr;
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle) == TRUE);

    StorageItemInfo item{};
    const int result = api.GetItem(handle, 0, &item);
    api.CloseStorage(handle);

    REQUIRE(result == GET_ITEM_OK);
    REQUIRE(item.Path[0] == 0);
    REQUIRE(item.Size == 9);
    REQUIRE(item.PackedSize == 8);
    REQUIRE(destroyed);
    UnloadSubModule();
}

TEST_CASE("common Observer: an oversized wide producer is rejected and zeroed", "[common][abi]")
{
    const auto api = load_api();
    const std::array<uint8_t, 4> bytes{'F', 'A', 'K', 'E'};
    const test::support::temporary_file source("common-wide-oversize", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);

    bool destroyed = false;
    auto owned = std::make_unique<scripted_wide_index>();
    owned->selected = scripted_wide_index::mode::oversize;
    owned->destroyed = &destroyed;
    state.storage_factory = [&owned] { return std::move(owned); };

    StorageOpenParams params{};
    params.StructSize = sizeof(params);
    params.FilePath = source.path().c_str();
    StorageGeneralInfo info{};
    HANDLE handle = nullptr;
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle) == TRUE);

    StorageItemInfo item;
    std::memset(&item, 0x7f, sizeof(item));
    const int result = api.GetItem(handle, 0, &item);
    api.CloseStorage(handle);

    REQUIRE(result == GET_ITEM_ERROR);
    const auto *record = reinterpret_cast<const unsigned char *>(&item);
    for (size_t i = 0; i < sizeof(item); ++i) {
        REQUIRE(record[i] == 0);
    }
    REQUIRE(destroyed);
    UnloadSubModule();
}

TEST_CASE("common Observer: GetItem retains the legacy metadata fallback", "[common][abi]")
{
    const auto api = load_api();
    const std::array<uint8_t, 4> bytes{'F', 'A', 'K', 'E'};
    const test::support::temporary_file source("common-wide-legacy", bytes);
    test::support::fake_extractor_state state;
    const test::support::fake_extractor_scope scope(state);
    state.files.push_back(test::support::fake_file_definition{
        .path = "legacy/entry.bin",
        .compressed_body_size_in_bytes = 3,
        .uncompressed_body_size_in_bytes = 3,
    });

    StorageOpenParams params{};
    params.StructSize = sizeof(params);
    params.FilePath = source.path().c_str();
    StorageGeneralInfo info{};
    HANDLE handle = nullptr;
    REQUIRE(api.OpenStorage(params, &handle, &info) == SOR_SUCCESS);
    REQUIRE(api.PrepareFiles(handle) == TRUE);

    StorageItemInfo item{};
    const int result = api.GetItem(handle, 0, &item);
    api.CloseStorage(handle);

    REQUIRE(result == GET_ITEM_OK);
    REQUIRE(std::wstring(item.Path) == L"legacy\\entry.bin");
    REQUIRE(item.Size == 3);
    REQUIRE(item.PackedSize == 3);
    UnloadSubModule();
}
