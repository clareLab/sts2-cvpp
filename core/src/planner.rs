use std::panic::{AssertUnwindSafe, catch_unwind};
use std::{mem, ptr, slice};

struct Node {
    action: u32,
    parent: usize,
    children: std::ops::Range<usize>,
    visits: u32,
    value: f64,
    depth: u16,
    expanded: bool,
    closed: bool,
}

impl Node {
    fn new(action: u32, parent: usize, depth: u16) -> Self {
        Self {
            action,
            parent,
            children: 0..0,
            visits: 0,
            value: 0.0,
            depth,
            expanded: false,
            closed: false,
        }
    }
}

pub struct Planner {
    nodes: Vec<Node>,
    current: Option<usize>,
    limit: usize,
    depth: u16,
    simulations: u32,
    best: i64,
    bounded: bool,
    poisoned: bool,
}

impl Planner {
    fn new(limit: u32, depth: u16) -> Option<Self> {
        if !(1..=1_000_000).contains(&limit) || !(1..=256).contains(&depth) {
            return None;
        }
        Some(Self {
            nodes: vec![Node::new(0, 0, 0)],
            current: None,
            limit: limit as usize,
            depth,
            simulations: 0,
            best: 0,
            bounded: false,
            poisoned: false,
        })
    }

    fn next(&mut self, output: &mut [u32]) -> i32 {
        if self.current.is_some() || self.poisoned {
            return -2;
        }
        if self.nodes[0].closed {
            return -1;
        }
        let mut index = 0;
        while self.nodes[index].expanded {
            let parent = &self.nodes[index];
            let exploration = f64::from(parent.visits.max(1)).ln();
            let mut selected = None;
            let mut best = f64::NEG_INFINITY;
            for child in parent.children.clone() {
                let node = &self.nodes[child];
                if node.closed {
                    continue;
                }
                let score = if node.visits == 0 {
                    f64::INFINITY
                } else {
                    let visits = f64::from(node.visits);
                    node.value / visits + (2.0 * exploration / visits).sqrt()
                };
                if selected.is_none() || score > best {
                    selected = Some(child);
                    best = score;
                }
            }
            let Some(child) = selected else {
                return -2;
            };
            index = child;
        }
        let depth = usize::from(self.nodes[index].depth);
        if output.len() < depth {
            return -3;
        }
        self.current = Some(index);
        for position in (0..depth).rev() {
            output[position] = self.nodes[index].action;
            index = self.nodes[index].parent;
        }
        i32::try_from(depth).unwrap_or(-2)
    }

    fn observe(&mut self, score: i32, terminal: bool, actions: &[u32]) -> i32 {
        if score < 0 || self.poisoned || (terminal && !actions.is_empty()) {
            return -2;
        }
        let Some(mut index) = self.current.take() else {
            return -2;
        };
        let depth = self.nodes[index].depth;
        let remaining = self.limit - self.nodes.len();
        let count = if depth < self.depth {
            actions.len().min(remaining)
        } else {
            0
        };
        self.bounded |= count < actions.len();
        let start = self.nodes.len();
        self.nodes.extend(
            actions[..count]
                .iter()
                .map(|&action| Node::new(action, index, depth + 1)),
        );
        self.nodes[index].children = start..self.nodes.len();
        self.nodes[index].expanded = true;
        self.nodes[index].closed = count == 0;
        self.simulations += 1;
        self.best = self.best.max(i64::from(score));
        let reward = f64::from(score) / (f64::from(score) + 100.0);
        loop {
            let mut children = self.nodes[index].children.clone();
            let closed =
                self.nodes[index].expanded && children.all(|child| self.nodes[child].closed);
            let node = &mut self.nodes[index];
            node.visits += 1;
            node.value += reward;
            node.closed = closed;
            if index == 0 {
                break;
            }
            index = node.parent;
        }
        0
    }

    fn guard(&mut self, operation: impl FnOnce(&mut Self) -> i32) -> i32 {
        catch_unwind(AssertUnwindSafe(|| operation(self))).unwrap_or_else(|_| {
            self.poisoned = true;
            -4
        })
    }
}

#[repr(C)]
#[derive(Default)]
pub struct PlannerStats {
    nodes: u32,
    simulations: u32,
    bounded: u32,
    reserved: u32,
    best: i64,
    memory_bytes: u64,
}

#[unsafe(no_mangle)]
extern "C" fn cvpp_planner_create(limit: u32, depth: u16) -> *mut Planner {
    catch_unwind(|| {
        Planner::new(limit, depth).map_or(ptr::null_mut(), |p| Box::into_raw(Box::new(p)))
    })
    .unwrap_or(ptr::null_mut())
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_planner_free(planner: *mut Planner) {
    if !planner.is_null() {
        drop(unsafe { Box::from_raw(planner) });
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_planner_next(
    planner: *mut Planner,
    output: *mut u32,
    capacity: u32,
) -> i32 {
    let Some(planner) = (unsafe { planner.as_mut() }) else {
        return -2;
    };
    if output.is_null() || capacity > 256 {
        return -2;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, capacity as usize) };
    planner.guard(|p| p.next(output))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_planner_observe(
    planner: *mut Planner,
    score: i32,
    terminal: u32,
    actions: *const u32,
    count: u32,
) -> i32 {
    let Some(planner) = (unsafe { planner.as_mut() }) else {
        return -2;
    };
    if count > 4096 || terminal > 1 || (count > 0 && actions.is_null()) {
        return -2;
    }
    let actions = if count == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(actions, count as usize) }
    };
    planner.guard(|p| p.observe(score, terminal == 1, actions))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_planner_stats(planner: *const Planner) -> PlannerStats {
    let Some(planner) = (unsafe { planner.as_ref() }) else {
        return PlannerStats::default();
    };
    PlannerStats {
        nodes: u32::try_from(planner.nodes.len()).unwrap_or(u32::MAX),
        simulations: planner.simulations,
        bounded: u32::from(planner.bounded),
        reserved: 0,
        best: planner.best,
        memory_bytes: (mem::size_of::<Planner>()
            + planner.nodes.capacity() * mem::size_of::<Node>()) as u64,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explores_finite_tree_and_prefers_successful_rollouts() {
        let mut planner = Planner::new(64, 4).unwrap();
        let mut path = [0; 4];
        let mut seen = Vec::new();
        loop {
            let depth = planner.next(&mut path);
            if depth == -1 {
                break;
            }
            let depth = usize::try_from(depth).unwrap();
            seen.push(path[..depth].to_vec());
            let score = i32::try_from(path[..depth].iter().sum::<u32>()).unwrap();
            assert_eq!(
                planner.observe(score, depth == 3, if depth == 3 { &[] } else { &[1, 2] }),
                0
            );
        }
        seen.sort();
        seen.dedup();
        assert_eq!(seen.len(), 15);
        assert_eq!(planner.best, 6);
        assert!(!planner.bounded);
    }

    #[test]
    fn enforces_protocol_capacity_and_budgets() {
        assert!(Planner::new(0, 4).is_none());
        assert!(Planner::new(8, 257).is_none());
        let mut planner = Planner::new(3, 2).unwrap();
        let mut path = [0; 2];
        assert_eq!(planner.observe(0, false, &[]), -2);
        assert_eq!(planner.next(&mut path), 0);
        assert_eq!(planner.next(&mut path), -2);
        assert_eq!(planner.observe(7, false, &[1, 2, 3]), 0);
        assert_eq!(planner.next(&mut []), -3);
        assert_eq!(planner.next(&mut path), 1);
        assert_eq!(planner.observe(-1, true, &[]), -2);
        assert_eq!(planner.observe(2, true, &[]), 0);
        assert_eq!(planner.next(&mut path), 1);
        assert_eq!(planner.observe(9, false, &[4]), 0);
        assert_eq!(planner.next(&mut path), -1);
        assert!(planner.bounded);
        assert_eq!(planner.best, 9);
    }
}
