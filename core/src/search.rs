use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::slice;

const DONE: i32 = -1;
const INVALID: i32 = -2;
const CAPACITY: i32 = -3;
const PANIC: i32 = -4;

struct Node {
    action: u32,
    depth: u16,
}

struct Search {
    frontier: Vec<Node>,
    path: [u32; 64],
    best_path: [u32; 64],
    current: Option<u16>,
    best: Option<(i64, u16)>,
    limit: u32,
    depth_limit: u16,
    allocated: u32,
    evaluated: u32,
    bounded: bool,
    poisoned: bool,
}

impl Search {
    fn new(limit: u32, depth_limit: u16) -> Option<Self> {
        if !(1..=1_000_000).contains(&limit) || !(1..=64).contains(&depth_limit) {
            return None;
        }
        let mut frontier = Vec::with_capacity(limit.min(64) as usize);
        frontier.push(Node {
            action: 0,
            depth: 0,
        });
        Some(Self {
            frontier,
            path: [0; 64],
            best_path: [0; 64],
            current: None,
            best: None,
            limit,
            depth_limit,
            allocated: 1,
            evaluated: 0,
            bounded: false,
            poisoned: false,
        })
    }

    fn best(&self, output: &mut [u32]) -> i32 {
        let Some((_, depth)) = self.best else {
            return DONE;
        };
        if output.len() < usize::from(depth) {
            return CAPACITY;
        }
        output[..usize::from(depth)].copy_from_slice(&self.best_path[..usize::from(depth)]);
        i32::from(depth)
    }

    fn next(&mut self, output: &mut [u32]) -> i32 {
        if self.current.is_some() || self.poisoned {
            return INVALID;
        }
        let Some(node) = self.frontier.last() else {
            return DONE;
        };
        let depth = node.depth;
        if output.len() < usize::from(depth) {
            return CAPACITY;
        }
        if depth > 0 {
            self.path[usize::from(depth) - 1] = node.action;
        }
        output[..usize::from(depth)].copy_from_slice(&self.path[..usize::from(depth)]);
        self.frontier.pop();
        self.current = Some(depth);
        i32::from(depth)
    }

    fn observe(&mut self, score: i64, solution: bool, actions: &[u32]) -> i32 {
        if self.poisoned || (solution && !actions.is_empty()) {
            return INVALID;
        }
        let Some(depth) = self.current.take() else {
            return INVALID;
        };
        self.evaluated += 1;
        if solution
            && self
                .best
                .is_none_or(|(best, previous)| score > best || (score == best && depth < previous))
        {
            self.best = Some((score, depth));
            self.best_path[..usize::from(depth)].copy_from_slice(&self.path[..usize::from(depth)]);
        }
        let remaining = (self.limit - self.allocated) as usize;
        let count = if depth < self.depth_limit {
            actions.len().min(remaining)
        } else {
            0
        };
        self.bounded |= count < actions.len();
        self.allocated += u32::try_from(count).unwrap_or(self.limit);
        for &action in actions[..count].iter().rev() {
            self.frontier.push(Node {
                action,
                depth: depth + 1,
            });
        }
        0
    }

    fn guard(&mut self, operation: impl FnOnce(&mut Self) -> i32) -> i32 {
        if let Ok(result) = catch_unwind(AssertUnwindSafe(|| operation(self))) {
            result
        } else {
            self.poisoned = true;
            PANIC
        }
    }
}

#[derive(Default)]
#[repr(C)]
struct SearchStats {
    allocated: u32,
    evaluated: u32,
    bounded: u32,
    best_found: u32,
    best_score: i64,
    memory_bytes: u64,
}

#[unsafe(no_mangle)]
extern "C" fn cvpp_search_create(limit: u32, depth: u16) -> *mut Search {
    catch_unwind(|| {
        Search::new(limit, depth).map_or(ptr::null_mut(), |search| Box::into_raw(Box::new(search)))
    })
    .unwrap_or(ptr::null_mut())
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_free(search: *mut Search) {
    if !search.is_null() {
        drop(unsafe { Box::from_raw(search) });
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_next(search: *mut Search, output: *mut u32, capacity: u32) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if output.is_null() || capacity > 64 {
        return INVALID;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, capacity as usize) };
    search.guard(|search| search.next(output))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_observe(
    search: *mut Search,
    score: i64,
    solution: u32,
    actions: *const u32,
    count: u32,
) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if count > 4096 || solution > 1 || (count > 0 && actions.is_null()) {
        return INVALID;
    }
    let actions = if count == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(actions, count as usize) }
    };
    search.guard(|search| search.observe(score, solution == 1, actions))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_best(search: *mut Search, output: *mut u32, capacity: u32) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if output.is_null() || capacity > 64 || search.poisoned {
        return INVALID;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, capacity as usize) };
    search.guard(|search| search.best(output))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_stats(search: *const Search) -> SearchStats {
    let Some(search) = (unsafe { search.as_ref() }) else {
        return SearchStats::default();
    };
    SearchStats {
        allocated: search.allocated,
        evaluated: search.evaluated,
        bounded: u32::from(search.bounded),
        best_found: u32::from(search.best.is_some()),
        best_score: search.best.map_or(i64::MIN, |(score, _)| score),
        memory_bytes: (size_of::<Search>() + search.frontier.capacity() * size_of::<Node>()) as u64,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_invalid_limits_and_null_handles() {
        assert!(Search::new(0, 1).is_none());
        assert!(Search::new(1_000_001, 1).is_none());
        assert!(Search::new(10, 0).is_none());
        assert!(Search::new(10, 65).is_none());
        assert_eq!(
            unsafe { cvpp_search_next(ptr::null_mut(), ptr::null_mut(), 0) },
            INVALID
        );
        assert_eq!(
            unsafe { cvpp_search_observe(ptr::null_mut(), 0, 0, ptr::null(), 0) },
            INVALID
        );
    }

    #[test]
    fn contains_panics_and_rejects_later_work() {
        let mut search = Search::new(8, 4).unwrap();
        assert_eq!(search.guard(|_| panic!("test panic")), PANIC);
        assert_eq!(search.next(&mut [0; 4]), INVALID);
        assert_eq!(search.observe(0, true, &[]), INVALID);
    }

    #[test]
    fn explores_dynamic_tree_and_preserves_best_path() {
        let mut search = Search::new(32, 4).unwrap();
        let mut path = [0; 4];
        while let length @ 0.. = search.next(&mut path) {
            let route = &path[..usize::try_from(length).unwrap()];
            let (score, solution, actions): (i64, bool, &[u32]) = match route {
                [] => (0, false, &[10, 20]),
                [10] => (0, false, &[11, 12]),
                [10, 11] => (-5, true, &[]),
                [10, 12] => (12, true, &[]),
                [20] => (7, true, &[]),
                _ => panic!("Unexpected route"),
            };
            assert_eq!(search.observe(score, solution, actions), 0);
        }
        let (score, _) = search.best.unwrap();
        assert_eq!(score, 12);
        assert_eq!(search.best(&mut path), 2);
        assert_eq!(&path[..2], &[10, 12]);
        assert_eq!(search.evaluated, 5);
        assert!(!search.bounded);
    }

    #[test]
    fn budget_preserves_completed_solution() {
        let mut search = Search::new(2, 4).unwrap();
        let mut path = [0; 4];
        assert_eq!(search.next(&mut path), 0);
        assert_eq!(search.observe(0, false, &[1, 2, 3]), 0);
        assert_eq!(search.next(&mut path), 1);
        assert_eq!(path[0], 1);
        assert_eq!(search.observe(-10, true, &[]), 0);
        assert_eq!(search.next(&mut path), DONE);
        assert!(search.bounded);
        assert_eq!(search.allocated, 2);
        assert_eq!(search.best.unwrap().0, -10);
    }

    #[test]
    fn invalid_protocol_and_small_buffers_do_not_consume_work() {
        let mut search = Search::new(8, 4).unwrap();
        let mut path = [0; 4];
        assert_eq!(search.observe(0, true, &[]), INVALID);
        assert_eq!(search.next(&mut path), 0);
        assert_eq!(search.next(&mut path), INVALID);
        assert_eq!(search.observe(0, true, &[1]), INVALID);
        assert_eq!(search.observe(0, false, &[42]), 0);
        assert_eq!(search.next(&mut []), CAPACITY);
        assert_eq!(search.next(&mut path), 1);
        assert_eq!(path[0], 42);
    }

    #[test]
    fn depth_limit_and_tie_breaking_are_deterministic() {
        let mut search = Search::new(16, 2).unwrap();
        let mut path = [0; 2];
        assert_eq!(search.next(&mut path), 0);
        search.observe(0, false, &[1, 2, 3]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(0, false, &[4, 5]);
        assert_eq!(search.next(&mut path), 2);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), 2);
        search.observe(0, false, &[6]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), DONE);
        assert!(search.bounded);
        assert_eq!(search.best(&mut path), 1);
        assert_eq!(path[0], 2);
    }

    #[test]
    fn wide_and_deep_traversals_match_recursive_reference() {
        #[derive(Default)]
        struct Reference {
            generated: u32,
            evaluated: u32,
            bounded: bool,
            best: Option<(i64, Vec<u32>)>,
            paths: Vec<Vec<u32>>,
        }

        fn fixture(seed: u32, path: &[u32]) -> (i64, bool, Vec<u32>) {
            let hash = path.iter().fold(seed, |hash, action| {
                hash.wrapping_mul(1_664_525).wrapping_add(*action)
            });
            let solution = hash % 7 == 0 || path.len() >= 5;
            let actions = if solution {
                vec![]
            } else {
                (0..hash % 4).map(|index| index * 13 + 1).collect()
            };
            (i64::from(hash % 31) - 15, solution, actions)
        }

        fn visit(state: &mut Reference, seed: u32, limit: u32, depth: usize, path: &mut Vec<u32>) {
            state.evaluated += 1;
            state.paths.push(path.clone());
            let (score, solution, actions) = fixture(seed, path);
            if solution
                && state.best.as_ref().is_none_or(|(best, previous)| {
                    score > *best || (score == *best && path.len() < previous.len())
                })
            {
                state.best = Some((score, path.clone()));
            }
            let count = if path.len() < depth {
                actions.len().min((limit - state.generated) as usize)
            } else {
                0
            };
            state.bounded |= count < actions.len();
            state.generated += u32::try_from(count).unwrap();
            for action in actions.into_iter().take(count) {
                path.push(action);
                visit(state, seed, limit, depth, path);
                path.pop();
            }
        }

        for seed in 0..32 {
            for limit in [1, 2, 3, 8, 16, 127, 2048] {
                for depth in [1, 3, 6] {
                    let mut reference = Reference {
                        generated: 1,
                        ..Reference::default()
                    };
                    visit(&mut reference, seed, limit, depth.into(), &mut vec![]);
                    let mut search = Search::new(limit, depth).unwrap();
                    let mut path = [0; 64];
                    for expected in &reference.paths {
                        let length = search.next(&mut path);
                        assert_eq!(usize::try_from(length).unwrap(), expected.len());
                        assert_eq!(&path[..expected.len()], expected);
                        let (score, solution, actions) = fixture(seed, expected);
                        assert_eq!(search.observe(score, solution, &actions), 0);
                    }
                    assert_eq!(search.next(&mut path), DONE);
                    assert_eq!(search.allocated, reference.generated);
                    assert_eq!(search.evaluated, reference.evaluated);
                    assert_eq!(search.bounded, reference.bounded);
                    if let Some((score, expected)) = reference.best {
                        assert_eq!(search.best.unwrap().0, score);
                        assert_eq!(
                            usize::try_from(search.best(&mut path)).unwrap(),
                            expected.len()
                        );
                        assert_eq!(&path[..expected.len()], expected);
                    } else {
                        assert_eq!(search.best(&mut path), DONE);
                    }
                }
            }
        }
    }

    #[test]
    fn large_budget_keeps_only_pending_branches_in_memory() {
        let mut search = Search::new(1_000_000, 18).unwrap();
        let mut path = [0; 18];
        while let length @ 0.. = search.next(&mut path) {
            let length = usize::try_from(length).unwrap();
            let score = path[..length]
                .iter()
                .fold(0, |score, action| score * 2 + i64::from(*action));
            let actions: &[u32] = if length == 18 { &[] } else { &[0, 1] };
            assert_eq!(search.observe(score, length == 18, actions), 0);
        }
        assert_eq!(search.evaluated, 524_287);
        assert_eq!(search.best.unwrap().0, 262_143);
        assert_eq!(search.best(&mut path), 18);
        assert_eq!(path, [1; 18]);
        assert!(!search.bounded);
        let stats = unsafe { cvpp_search_stats(&raw const search) };
        assert!(stats.memory_bytes < 2048);
    }
}
