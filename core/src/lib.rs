mod search;

pub const ABI_VERSION: u32 = 2;

#[unsafe(no_mangle)]
pub extern "C" fn cvpp_abi_version() -> u32 {
    ABI_VERSION
}
