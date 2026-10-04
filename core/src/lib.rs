mod search;

pub const ABI_VERSION: u32 = 3;

#[unsafe(no_mangle)]
pub extern "C" fn cvpp_abi_version() -> u32 {
    ABI_VERSION
}
