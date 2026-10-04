use std::ptr;
use std::slice;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

const PAGE_WORDS: usize = 256;
static LIVE_BYTES: AtomicU64 = AtomicU64::new(0);

struct Page {
    words: Vec<u64>,
}

impl Page {
    fn capture(source: &[u64]) -> Option<Arc<Self>> {
        let mut words = Vec::new();
        words.try_reserve_exact(source.len()).ok()?;
        words.extend_from_slice(source);
        LIVE_BYTES.fetch_add(size_of_val(source) as u64, Ordering::Relaxed);
        Some(Arc::new(Self { words }))
    }
}

impl Drop for Page {
    fn drop(&mut self) {
        LIVE_BYTES.fetch_sub(
            (self.words.len() * size_of::<u64>()) as u64,
            Ordering::Relaxed,
        );
    }
}

struct Snapshot {
    pages: Vec<Arc<Page>>,
    count: usize,
}

impl Snapshot {
    fn capture(source: &[u64], budget: usize, parent: Option<&Self>) -> Option<Self> {
        if source.is_empty() || size_of_val(source) > budget {
            return None;
        }
        let mut pages = Vec::new();
        pages
            .try_reserve_exact(source.len().div_ceil(PAGE_WORDS))
            .ok()?;
        for (index, words) in source.chunks(PAGE_WORDS).enumerate() {
            let previous = parent.and_then(|snapshot| snapshot.pages.get(index));
            let page = match previous {
                Some(page) if page.words == words => Arc::clone(page),
                _ => Page::capture(words)?,
            };
            pages.push(page);
        }
        Some(Self {
            pages,
            count: source.len(),
        })
    }

    fn restore(&self, output: &mut [u64]) -> bool {
        if output.len() != self.count {
            return false;
        }
        for (page, chunk) in self.pages.iter().zip(output.chunks_mut(PAGE_WORDS)) {
            chunk.copy_from_slice(&page.words);
        }
        true
    }

    fn shared_bytes(&self) -> usize {
        self.pages
            .iter()
            .filter(|page| Arc::strong_count(page) > 1)
            .map(|page| page.words.len() * size_of::<u64>())
            .sum()
    }
}

unsafe fn capture(
    parent: Option<&Snapshot>,
    source: *const u64,
    count: u32,
    budget: u32,
) -> *mut Snapshot {
    if source.is_null()
        || count == 0
        || u64::from(count) * 8 > u64::from(budget)
        || u64::from(count) > (isize::MAX as u64) / 8
    {
        return ptr::null_mut();
    }
    let source = unsafe { slice::from_raw_parts(source, count as usize) };
    Snapshot::capture(source, budget as usize, parent).map_or(ptr::null_mut(), |snapshot| {
        Box::into_raw(Box::new(snapshot))
    })
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_capture(
    source: *const u64,
    count: u32,
    budget: u32,
) -> *mut Snapshot {
    unsafe { capture(None, source, count, budget) }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_capture_from(
    parent: *const Snapshot,
    source: *const u64,
    count: u32,
    budget: u32,
) -> *mut Snapshot {
    if parent.is_null() {
        return ptr::null_mut();
    }
    unsafe { capture(Some(&*parent), source, count, budget) }
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
    if count as usize != snapshot.count {
        return -1;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, count as usize) };
    i32::from(snapshot.restore(output)) - 1
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_free(snapshot: *mut Snapshot) {
    if !snapshot.is_null() {
        drop(unsafe { Box::from_raw(snapshot) });
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_snapshot_shared_bytes(snapshot: *const Snapshot) -> u64 {
    if snapshot.is_null() {
        return 0;
    }
    unsafe { &*snapshot }.shared_bytes() as u64
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
        let snapshot = Snapshot::capture(&source, 32, None).unwrap();
        source.fill(7);
        for _ in 0..100 {
            assert!(snapshot.restore(&mut source));
            assert_eq!(source, [0, u64::MAX, 42, 0x7ff8_0000_0000_0001]);
            source.fill(0);
        }
    }

    #[test]
    fn shares_unchanged_pages_without_parent_chains() {
        let mut source = vec![1; PAGE_WORDS * 3 + 3];
        let root = Snapshot::capture(&source, 8192, None).unwrap();
        source[PAGE_WORDS] = 2;
        let branch = Snapshot::capture(&source, 8192, Some(&root)).unwrap();
        assert_eq!(branch.shared_bytes(), (PAGE_WORDS * 2 + 3) * 8);
        assert!(!Arc::ptr_eq(&root.pages[1], &branch.pages[1]));
        assert_eq!(root.pages[1].words[0], 1);
        drop(root);
        assert_eq!(branch.shared_bytes(), 0);
        let mut output = vec![0; source.len()];
        assert!(branch.restore(&mut output));
        assert_eq!(source, output);
    }

    #[test]
    fn handles_growing_and_shrinking_snapshots() {
        let root = Snapshot::capture(&vec![1; PAGE_WORDS + 1], 4096, None).unwrap();
        let large = Snapshot::capture(&vec![1; PAGE_WORDS * 2], 4096, Some(&root)).unwrap();
        assert!(Arc::ptr_eq(&root.pages[0], &large.pages[0]));
        assert!(!Arc::ptr_eq(&root.pages[1], &large.pages[1]));
        let small = Snapshot::capture(&[1, 2, 3], 24, Some(&large)).unwrap();
        drop(large);
        drop(root);
        let mut output = [0; 3];
        assert!(small.restore(&mut output));
        assert_eq!(output, [1, 2, 3]);
    }

    #[test]
    fn rejects_limits_without_touching_output() {
        assert!(Snapshot::capture(&[], 0, None).is_none());
        assert!(Snapshot::capture(&[1, 2], 15, None).is_none());
        let snapshot = Snapshot::capture(&[1, 2], 16, None).unwrap();
        let mut output = [3];
        assert!(!snapshot.restore(&mut output));
        assert_eq!(output, [3]);
    }

    #[test]
    fn ffi_validates_and_releases() {
        unsafe {
            assert!(cvpp_snapshot_capture(ptr::null(), 1, 8).is_null());
            assert!(cvpp_snapshot_capture([1].as_ptr(), u32::MAX, u32::MAX).is_null());
            assert!(cvpp_snapshot_capture_from(ptr::null(), [1].as_ptr(), 1, 8).is_null());
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
