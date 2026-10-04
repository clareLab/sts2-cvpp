use std::ptr;
use std::slice;
use std::sync::atomic::{AtomicU64, Ordering};

static LIVE_BYTES: AtomicU64 = AtomicU64::new(0);

struct Snapshot {
    words: Vec<u64>,
}

impl Snapshot {
    fn capture(source: &[u64], budget: usize) -> Option<Self> {
        if source.is_empty() || size_of_val(source) > budget {
            return None;
        }
        let mut words = Vec::new();
        words.try_reserve_exact(source.len()).ok()?;
        words.extend_from_slice(source);
        Some(Self { words })
    }

    fn restore(&self, output: &mut [u64]) -> bool {
        if output.len() != self.words.len() {
            return false;
        }
        output.copy_from_slice(&self.words);
        true
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_capture(
    source: *const u64,
    count: u32,
    budget: u32,
) -> *mut Snapshot {
    if source.is_null() || count == 0 || u64::from(count) * 8 > u64::from(budget) {
        return ptr::null_mut();
    }
    let source = unsafe { slice::from_raw_parts(source, count as usize) };
    Snapshot::capture(source, budget as usize).map_or(ptr::null_mut(), |snapshot| {
        LIVE_BYTES.fetch_add(u64::from(count) * 8, Ordering::Relaxed);
        Box::into_raw(Box::new(snapshot))
    })
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_restore(
    snapshot: *const Snapshot,
    output: *mut u64,
    count: u32,
) -> i32 {
    if snapshot.is_null() || output.is_null() || count == 0 {
        return -1;
    }
    let snapshot = unsafe { &*snapshot };
    if count as usize != snapshot.words.len() {
        return -1;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, count as usize) };
    i32::from(snapshot.restore(output)) - 1
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_free(snapshot: *mut Snapshot) {
    if !snapshot.is_null() {
        let snapshot = unsafe { Box::from_raw(snapshot) };
        LIVE_BYTES.fetch_sub((snapshot.words.len() as u64) * 8, Ordering::Relaxed);
        drop(snapshot);
    }
}

#[unsafe(no_mangle)]
extern "C" fn cvpp_snapshot_live_bytes() -> u64 {
    LIVE_BYTES.load(Ordering::Relaxed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn owns_bytes_and_restores_repeatedly() {
        let mut source = [0, u64::MAX, 42, 0x7ff8_0000_0000_0001];
        let snapshot = Snapshot::capture(&source, 32).unwrap();
        source.fill(7);
        for _ in 0..100 {
            assert!(snapshot.restore(&mut source));
            assert_eq!(source, [0, u64::MAX, 42, 0x7ff8_0000_0000_0001]);
            source.fill(0);
        }
    }

    #[test]
    fn rejects_limits_without_touching_output() {
        assert!(Snapshot::capture(&[], 0).is_none());
        assert!(Snapshot::capture(&[1, 2], 15).is_none());
        let snapshot = Snapshot::capture(&[1, 2], 16).unwrap();
        let mut output = [3];
        assert!(!snapshot.restore(&mut output));
        assert_eq!(output, [3]);
    }

    #[test]
    fn ffi_validates_and_releases() {
        unsafe {
            assert!(cvpp_snapshot_capture(ptr::null(), 1, 8).is_null());
            assert!(cvpp_snapshot_capture([1].as_ptr(), u32::MAX, u32::MAX).is_null());
            let snapshot = cvpp_snapshot_capture([1, 2].as_ptr(), 2, 16);
            assert!(!snapshot.is_null());
            let mut output = [3, 4];
            assert_eq!(cvpp_snapshot_restore(snapshot, output.as_mut_ptr(), 1), -1);
            assert_eq!(output, [3, 4]);
            assert_eq!(cvpp_snapshot_restore(snapshot, output.as_mut_ptr(), 2), 0);
            assert_eq!(output, [1, 2]);
            cvpp_snapshot_free(snapshot);
            cvpp_snapshot_free(ptr::null_mut());
        }
    }
}
